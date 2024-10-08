using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Popups;
using Content.Shared.UserInterface;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Robust.Shared.Audio;
using Content.Server.Access.Systems;
using Content.Server.Crayon;
using Content.Shared.Hands;
using Robust.Shared.Audio.Systems;
using static Content.Shared.Paper.SharedPaperComponent;
using Content.Shared.Verbs;
using Content.Shared.Ghost; // Frontier: avoid ghost interaction
using Content.Shared.Timing; // Frontier: stamp reapplication, avoiding spam

namespace Content.Server.Paper
{
    public sealed class PaperSystem : EntitySystem
    {
        [Dependency] private readonly IAdminLogManager _adminLogger = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
        [Dependency] private readonly SharedInteractionSystem _interaction = default!;
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly TagSystem _tagSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
        [Dependency] private readonly MetaDataSystem _metaSystem = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly IdCardSystem _idCardSystem = default!;
        [Dependency] private readonly UseDelaySystem _useDelay = default!; // Frontier

        private const int ReapplyLimit = 10; // Frontier: limits on reapplied stamps
        private const int StampLimit = 100; // Frontier: limits on total stamps on a page (should be able to get a signature from everybody on the server on a page)

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<PaperComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<PaperComponent, BeforeActivatableUIOpenEvent>(BeforeUIOpen);
            SubscribeLocalEvent<PaperComponent, ExaminedEvent>(OnExamined);
            SubscribeLocalEvent<PaperComponent, InteractUsingEvent>(OnInteractUsing);
            SubscribeLocalEvent<PaperComponent, PaperInputTextMessage>(OnInputTextMessage);

            SubscribeLocalEvent<ActivateOnPaperOpenedComponent, PaperWriteEvent>(OnPaperWrite);

            SubscribeLocalEvent<PaperComponent, MapInitEvent>(OnMapInit);

            SubscribeLocalEvent<PaperComponent, GetVerbsEvent<AlternativeVerb>>(AddSignVerb); // Frontier - Sign verb hook
        }

        private void OnMapInit(EntityUid uid, PaperComponent paperComp, MapInitEvent args)
        {
            if (!string.IsNullOrEmpty(paperComp.Content))
            {
                paperComp.Content = Loc.GetString(paperComp.Content);
            }
        }

        private void OnInit(EntityUid uid, PaperComponent paperComp, ComponentInit args)
        {
            paperComp.Mode = PaperAction.Read;
            UpdateUserInterface(uid, paperComp);

            if (TryComp<AppearanceComponent>(uid, out var appearance))
            {
                if (paperComp.Content != "")
                    _appearance.SetData(uid, PaperVisuals.Status, PaperStatus.Written, appearance);

                if (paperComp.StampState != null)
                    _appearance.SetData(uid, PaperVisuals.Stamp, paperComp.StampState, appearance);
            }
        }

        private void BeforeUIOpen(EntityUid uid, PaperComponent paperComp, BeforeActivatableUIOpenEvent args)
        {
            paperComp.Mode = PaperAction.Read;
            UpdateUserInterface(uid, paperComp);
        }

        private void OnExamined(EntityUid uid, PaperComponent paperComp, ExaminedEvent args)
        {
            if (!args.IsInDetailsRange)
                return;

            using (args.PushGroup(nameof(PaperComponent)))
            {
                if (paperComp.Content != "")
                    args.PushMarkup(
                        Loc.GetString(
                            "paper-component-examine-detail-has-words", ("paper", uid)
                        )
                    );

                if (paperComp.StampedBy.Count > 0)
                {
                    // BEGIN FRONTIER MODIFICATION - Make stamps and signatures render separately.
                    // Separate into stamps and signatures.
                    var stamps = paperComp.StampedBy.FindAll(s => s.Type == StampType.RubberStamp);
                    var signatures = paperComp.StampedBy.FindAll(s => s.Type == StampType.Signature);

                    // If we have stamps, render them.
                    if (stamps.Count > 0)
                    {
                        var joined = string.Join(", ", stamps.Select(s => Loc.GetString(s.StampedName)));
                        args.PushMarkup(
                            Loc.GetString(
                                "paper-component-examine-detail-stamped-by",
                                ("paper", uid),
                                ("stamps", joined)
                            )
                        );
                    }

                    // Ditto for signatures.
                    if (signatures.Count > 0)
                    {
                        var joined = string.Join(", ", signatures.Select(s => s.StampedName));
                        args.PushMarkup(
                            Loc.GetString(
                                "paper-component-examine-detail-signed-by",
                                ("paper", uid),
                                ("stamps", joined)
                            )
                        );
                    }
                    // END FRONTIER MODIFICATION
                }
            }
        }

        private void OnInteractUsing(EntityUid uid, PaperComponent paperComp, InteractUsingEvent args)
        {
            // only allow editing if there are no stamps or when using a cyberpen
            var editable = paperComp.StampedBy.Count == 0 || _tagSystem.HasTag(args.Used, "WriteIgnoreStamps");
            if (_tagSystem.HasTag(args.Used, "Write") && editable)
            {
                if (paperComp.EditingDisabled)
                {
                    var paperEditingDisabledMessage = Loc.GetString("paper-tamper-proof-modified-message");
                    _popupSystem.PopupEntity(paperEditingDisabledMessage, uid, args.User);

                    args.Handled = true;
                    return;
                }
                var writeEvent = new PaperWriteEvent(uid, args.User);
                RaiseLocalEvent(args.Used, ref writeEvent);

                // Frontier - Restrict writing to entities with ActorComponent, players only
                if (!TryComp<ActorComponent>(args.User, out var actor))
                    return;

                paperComp.Mode = PaperAction.Write;
                _uiSystem.OpenUi(uid, PaperUiKey.Key, args.User);
                UpdateUserInterface(uid, paperComp);
                args.Handled = true;
                return;
            }

            // If a stamp, attempt to stamp paper
            if (TryComp<StampComponent>(args.Used, out var stampComp) &&
                !StampDelayed(args.Used)) // Frontier: check stamp is delayed
            {
                var stampInfo = GetStampInfo(stampComp); // Frontier: assign DisplayStampInfo before stamp
                if (_tagSystem.HasTag(args.Used, "Write"))
                    stampInfo.Type = StampType.Signature;
                if (TryStamp(uid, stampInfo, stampComp.StampState, paperComp))
                {
                    // End of Frontier modifications
                    // successfully stamped, play popup
                    var stampPaperOtherMessage = Loc.GetString("paper-component-action-stamp-paper-other",
                        ("user", args.User), ("target", args.Target), ("stamp", args.Used));

                    _popupSystem.PopupEntity(stampPaperOtherMessage, args.User,
                        Filter.PvsExcept(args.User, entityManager: EntityManager), true);
                    var stampPaperSelfMessage = Loc.GetString("paper-component-action-stamp-paper-self",
                        ("target", args.Target), ("stamp", args.Used));
                    _popupSystem.PopupEntity(stampPaperSelfMessage, args.User, args.User);

                    _audio.PlayPvs(stampComp.Sound, uid);

                    UpdateUserInterface(uid, paperComp);

                    DelayStamp(args.Used); // Frontier: prevent stamp spam
                }
            } // Frontier: added an indent level
        }

        private static StampDisplayInfo GetStampInfo(StampComponent stamp)
        {
            return new StampDisplayInfo
            {
                Reapply = stamp.Reapply, // Frontier
                StampedName = stamp.StampedName,
                StampedColor = stamp.StampedColor
            };
        }

        private void OnInputTextMessage(EntityUid uid, PaperComponent paperComp, PaperInputTextMessage args)
        {
            if (args.Text.Length <= paperComp.ContentSize)
            {
                paperComp.Content = args.Text;

                if (TryComp<AppearanceComponent>(uid, out var appearance))
                    _appearance.SetData(uid, PaperVisuals.Status, PaperStatus.Written, appearance);

                if (TryComp(uid, out MetaDataComponent? meta))
                    _metaSystem.SetEntityDescription(uid, "", meta);

                _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"{ToPrettyString(args.Actor):player} has written on {ToPrettyString(uid):entity} the following text: {args.Text}");

                _audio.PlayPvs(paperComp.Sound, uid);
            }

            paperComp.Mode = PaperAction.Read;
            UpdateUserInterface(uid, paperComp);
        }

        private void OnPaperWrite(EntityUid uid, ActivateOnPaperOpenedComponent comp, ref PaperWriteEvent args)
        {
            _interaction.UseInHandInteraction(args.User, uid);
        }

        /// <summary>
        ///     Accepts the name and state to be stamped onto the paper, returns true if successful.
        /// </summary>
        public bool TryStamp(EntityUid uid, StampDisplayInfo stampInfo, string spriteStampState, PaperComponent? paperComp = null)
        {
            if (!Resolve(uid, ref paperComp))
                return false;

            if (CanStamp(stampInfo, paperComp)) // Frontier: !paperComp.StampedBy.Contains(stampInfo) < CanStamp(stampInfo, paperComp)
            {
                paperComp.StampedBy.Add(stampInfo);
                if (paperComp.StampState == null && TryComp<AppearanceComponent>(uid, out var appearance))
                {
                    paperComp.StampState = spriteStampState;
                    // Would be nice to be able to display multiple sprites on the paper
                    // but most of the existing images overlap
                    _appearance.SetData(uid, PaperVisuals.Stamp, paperComp.StampState, appearance);
                }
            }

            return true;
        }

        // FRONTIER - stamp precondition
        private bool CanStamp(StampDisplayInfo stampInfo, PaperComponent paperComp)
        {
            if (paperComp.StampedBy.Count >= StampLimit)
                return false;
            if (stampInfo.Reapply)
                return paperComp.StampedBy.FindAll(x => x.Equals(stampInfo)).Count < ReapplyLimit;
            else
                return !paperComp.StampedBy.Contains(stampInfo); // Original precondition
        }

        // FRONTIER - stamp reapplication: checks if a given stamp is delayed
        private bool StampDelayed(EntityUid stampUid)
        {
            return TryComp<UseDelayComponent>(stampUid, out var delay) &&
                _useDelay.IsDelayed((stampUid, delay));
        }

        // FRONTIER - stamp reapplication: resets the delay on a given stamp
        private void DelayStamp(EntityUid stampUid)
        {
            if (TryComp<UseDelayComponent>(stampUid, out var delay))
                _useDelay.TryResetDelay(stampUid, false, delay);
        }

        // FRONTIER - Pen signing: Adds the sign verb for pen signing
        private void AddSignVerb(EntityUid uid, PaperComponent component, GetVerbsEvent<AlternativeVerb> args)
        {
            if (!args.CanAccess || !args.CanInteract)
                return;

            // Sanity check
            if (uid != args.Target || HasComp<GhostComponent>(args.User))
                return;

            // Pens have a `Write` tag.
            if (!args.Using.HasValue || !_tagSystem.HasTag(args.Using.Value, "Write"))
                return;

            AlternativeVerb verb = new()
            {
                Act = () =>
                {
                    TrySign(args.Target, args.User, args.Using.Value, component);
                },
                Text = Loc.GetString("paper-component-verb-sign")
                // Icon = Don't have an icon yet. Todo for later.
            };
            args.Verbs.Add(verb);
        }

        // FRONTIER - TrySign method, attempts to place a signature
        public bool TrySign(EntityUid paper, EntityUid signer, EntityUid pen, PaperComponent paperComp)
        {
            // Generate display information.
            StampDisplayInfo info = new StampDisplayInfo
            {
                Reapply = false, // Frontier
                StampedName = Name(signer),
                StampedColor = Color.FromHex("#333333"),
                Type = StampType.Signature
            };

            // Get Crayon component, and if present set custom color from crayon
            if (TryComp<CrayonComponent>(pen, out var crayon))
                info.StampedColor = crayon.Color;

            // Try stamp with the info, return false if failed.
            if (!StampDelayed(pen) && TryStamp(paper, info, "paper_stamp-nf-signature", paperComp)) // Frontier: add !StampDelayed(pen)
            {
                // Signing successful, popup time.
                _popupSystem.PopupEntity(
                    Loc.GetString(
                        "paper-component-action-signed-other",
                        ("user", signer),
                        ("target", paper)
                    ),
                    signer,
                    Filter.PvsExcept(signer, entityManager: EntityManager),
                    true
                );

                _popupSystem.PopupEntity(
                    Loc.GetString(
                        "paper-component-action-signed-self",
                        ("target", paper)
                    ),
                    signer,
                    signer
                );

                _audio.PlayPvs(paperComp.Sound, paper);

                _adminLogger.Add(LogType.Verb, LogImpact.Low,
                    $"{ToPrettyString(signer):player} has signed {ToPrettyString(paper):paper}.");

                UpdateUserInterface(paper, paperComp);

                // If this is a crayon, decrease # charges when actually used
                if (crayon is not null)
                    crayon.Charges -= 1;

                DelayStamp(pen); // prevent stamp spam

                return true;
            }

            return false;
        }

        public void SetContent(EntityUid uid, string content, PaperComponent? paperComp = null)
        {
            if (!Resolve(uid, ref paperComp))
                return;

            paperComp.Content = content.Trim() + '\n'; // Frontier: content<content.Trim()
            UpdateUserInterface(uid, paperComp);

            if (!TryComp<AppearanceComponent>(uid, out var appearance))
                return;

            var status = string.IsNullOrWhiteSpace(content)
                ? PaperStatus.Blank
                : PaperStatus.Written;

            _appearance.SetData(uid, PaperVisuals.Status, status, appearance);
        }

        public void UpdateUserInterface(EntityUid uid, PaperComponent? paperComp = null)
        {
            if (!Resolve(uid, ref paperComp))
                return;

            _uiSystem.SetUiState(uid, PaperUiKey.Key,
                new PaperBoundUserInterfaceState(paperComp.Content, paperComp.StampedBy, paperComp.Mode));
        }
    }

    /// <summary>
    /// Event fired when using a pen on paper, opening the UI.
    /// </summary>
    [ByRefEvent]
    public record struct PaperWriteEvent(EntityUid User, EntityUid Paper);
}
