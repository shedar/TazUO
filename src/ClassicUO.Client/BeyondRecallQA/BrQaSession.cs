// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.Gumps.GridContainers;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;

namespace ClassicUO.BeyondRecallQA
{
    internal sealed class BrQaSession : IDisposable
    {
        private static BrQaSession _instance;

        public static BrQaSession Instance => _instance;
        public static bool IsActive => _instance != null && _instance._config.Enabled;

        private readonly BrQaConfig _config;
        private readonly BrQaEventEmitter _emitter;
        private readonly BrQaPlan _plan;
        private readonly BrQaTargetDocument _targets;
        private readonly DateTimeOffset _deadline;
        private readonly Dictionary<string, ushort> _combatBaselines =
            new Dictionary<string, ushort>(StringComparer.Ordinal);
        private readonly List<uint> _gumpSwitches = new List<uint>();
        private readonly Dictionary<ushort, string> _gumpEntries = new Dictionary<ushort, string>();
        private DateTimeOffset _actionDeadline;
        private int _actionIndex;
        private bool _actionIssued;
        private bool _disposed;
        private bool _scenarioCompleted;
        private bool _scenarioFailed;
        private bool _settingsLoaded;
        private bool _assetsLoaded;
        private bool _windowReady;
        private bool _accountAuthenticated;
        private bool _characterEnteredWorld;
        private bool _serverSelected;
        private int _movementAcknowledgements;
        private int _movementBaseline;
        private int _movementIssued;
        private int _pathStepIndex;
        private int _gumpObservations;
        private int _gumpBaseline;
        private uint? _lastGumpId;
        private string _expectedJournal;
        private bool _journalObserved;
        private DateTimeOffset _delayUntil;

        public BrQaConfig Config => _config;
        public bool ScenarioCompleted => _scenarioCompleted;
        internal BrQaEventEmitter Emitter => _emitter;

        private BrQaSession(BrQaConfig config)
        {
            _config = config;
            _plan = BrQaPlan.Load(config.PlanPath);
            _targets = BrQaTargetDocument.Load(config);
            _emitter = new BrQaEventEmitter(config);
            _deadline = DateTimeOffset.UtcNow.AddSeconds(_plan.TimeoutSeconds);
        }

        public static BrQaSession Initialize(string[] args)
        {
            if (_instance != null)
                throw new InvalidOperationException("Beyond Recall QA session was initialized more than once.");

            var config = BrQaConfig.Parse(args);
            if (!config.Enabled)
                return null;

            _instance = new BrQaSession(config);
            _instance._emitter.EmitProcessStarted();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Log.Info("Beyond Recall QA session initialized.");
            return _instance;
        }

        private static void OnProcessExit(object sender, EventArgs eventArgs) => _instance?.Dispose();

        public void FailUnhandledException() => FailScenario("unhandled-exception");

        public void EmitSettingsLoaded()
        {
            if (_settingsLoaded || _scenarioFailed)
                return;

            var settings = Settings.GlobalSettings;
            if (settings == null || settings.IP != "127.0.0.1" || settings.Port != 2593 ||
                string.IsNullOrWhiteSpace(settings.ClientVersion) || settings.SaveAccount || settings.AutoLogin ||
                settings.Reconnect || !string.IsNullOrEmpty(settings.Password) ||
                !string.Equals(settings.IP + ":" + settings.Port, _config.ServerEndpoint, StringComparison.Ordinal) ||
                !string.Equals(settings.ClientVersion, _config.ClientVersion, StringComparison.Ordinal))
            {
                FailScenario("unsafe-settings");
                return;
            }

            _emitter.ServerEndpoint = settings.IP + ":" + settings.Port;
            _emitter.ClientVersion = settings.ClientVersion;
            _settingsLoaded = true;
            _emitter.EmitSettingsLoaded();
        }

        public void EmitLegalDataOpened()
        {
            if (!_scenarioFailed)
                _emitter.EmitLegalDataOpened();
        }

        public void EmitAssetsLoaded()
        {
            if (_assetsLoaded || _scenarioFailed)
                return;

            _assetsLoaded = true;
            _emitter.EmitAssetsLoaded();
        }

        public void EmitWindowReady()
        {
            if (_windowReady || _scenarioFailed)
                return;

            _windowReady = true;
            _emitter.EmitWindowReady();
        }

        public void EmitNetworkConnected()
        {
            if (!_scenarioFailed)
                _emitter.EmitNetworkConnected();
        }

        public void ObserveServerListReceived(int count)
        {
            if (_scenarioFailed)
                return;

            if (!_accountAuthenticated)
            {
                _accountAuthenticated = true;
                _emitter.EmitAccountAuthenticated();
            }

            _emitter.EmitServerListReceived(count);
        }

        public void EmitCharacterListReceived(int count)
        {
            if (!_scenarioFailed)
                _emitter.EmitCharacterListReceived(count);
        }

        public void EmitCharacterEnteredWorld()
        {
            if (_characterEnteredWorld || _scenarioFailed)
                return;

            _characterEnteredWorld = true;
            _emitter.EmitCharacterEnteredWorld();
        }

        public void EmitMovementAcknowledged(byte sequence)
        {
            if (_scenarioFailed)
                return;

            _movementAcknowledgements++;
            _emitter.EmitMovementAcknowledged(sequence);
        }

        public void EmitGumpOpened(uint gumpId)
        {
            if (_scenarioFailed)
                return;

            _gumpObservations++;
            _lastGumpId = gumpId;
            _emitter.EmitGumpOpened(gumpId);
        }

        public void ObserveJournal(string text)
        {
            if (_scenarioFailed || _expectedJournal == null || text == null ||
                !text.Contains(_expectedJournal, StringComparison.Ordinal))
            {
                return;
            }

            _journalObserved = true;
            var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            _emitter.EmitJournalObserved(sha256);
        }

        public void Update()
        {
            if (_disposed || _scenarioCompleted || _scenarioFailed)
                return;
            if (DateTimeOffset.UtcNow >= _deadline)
            {
                FailScenario("plan-timeout");
                return;
            }
            if (!_settingsLoaded || !_assetsLoaded || !_windowReady)
                return;
            if (_actionIndex >= _plan.Actions.Count)
            {
                FailScenario("plan-missing-exit");
                return;
            }

            var action = _plan.Actions[_actionIndex];
            if (_actionDeadline == default)
            {
                _actionDeadline = DateTimeOffset.UtcNow.AddSeconds(action.TimeoutSeconds);
                _emitter.EmitActionStarted(_actionIndex, action.Type, action.Target);
            }
            if (DateTimeOffset.UtcNow >= _actionDeadline)
            {
                FailScenario("action-timeout");
                return;
            }

            try
            {
                ExecuteAction(action);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or InvalidOperationException or
                    ArgumentException or OverflowException
            )
            {
                Log.Error($"Beyond Recall QA action failed: {exception}");
                FailScenario("action-failed");
            }
        }

        private void ExecuteAction(BrQaAction action)
        {
            switch (action.Type)
            {
                case "connect":
                    CompleteAction(action);
                    return;
                case "authenticate-from-secret":
                    Authenticate(action);
                    return;
                case "select-or-create-named-qa-character":
                    SelectQaCharacter(action);
                    return;
                case "wait-enter-world":
                    if (_characterEnteredWorld && World.Instance?.InGame == true)
                        CompleteAction(action);
                    return;
                case "walk-relative":
                    Walk(action, action.Direction, action.Steps);
                    return;
                case "walk-path":
                    WalkPath(action);
                    return;
                case "speak":
                case "say-to-mobile":
                    IssueSpeech(action, action.Message);
                    return;
                case "wait-journal":
                case "wait-system-message":
                    _expectedJournal ??= action.Contains;
                    if (_journalObserved)
                        CompleteAction(action);
                    return;
                case "open-paperdoll":
                    if (World.Instance?.Player != null)
                    {
                        GameActions.OpenPaperdoll(World.Instance, World.Instance.Player.Serial);
                        CompleteAction(action);
                    }
                    return;
                case "open-backpack":
                    if (World.Instance?.Player != null && GameActions.OpenBackpack(World.Instance))
                        CompleteAction(action);
                    return;
                case "open-skills":
                    if (World.Instance?.Player != null)
                    {
                        GameActions.OpenSkills(World.Instance);
                        CompleteAction(action);
                    }
                    return;
                case "send-server-command":
                    IssueSpeech(action, action.Command);
                    return;
                case "invoke-qa-checkpoint":
                    IssueSpeech(action, BuildCheckpointCommand(_config.SessionToken, action.Target));
                    return;
                case "wait-gump":
                    if (HasExpectedGump(action))
                        CompleteAction(action);
                    return;
                case "double-click-serial":
                    IssueEntityAction(
                        action,
                        (world, serial) => GameActions.DoubleClick(
                            world,
                            serial,
                            ignoreWarMode: true,
                            ignoreQueue: true
                        )
                    );
                    return;
                case "single-click-serial":
                    IssueEntityAction(action, GameActions.SingleClick);
                    return;
                case "attack-serial":
                    Attack(action);
                    return;
                case "cast-spell":
                    GameActions.CastSpell(action.SpellIndex.Value);
                    _emitter.EmitSpellCastRequested(action.SpellIndex.Value);
                    CompleteAction(action);
                    return;
                case "use-skill":
                    GameActions.UseSkill(action.SkillIndex.Value);
                    _emitter.EmitSkillUseRequested(action.SkillIndex.Value);
                    CompleteAction(action);
                    return;
                case "wait-target-cursor":
                    if (World.Instance?.TargetManager?.IsTargeting == true)
                        CompleteAction(action);
                    return;
                case "target-serial":
                    TargetSerial(action);
                    return;
                case "target-location":
                    TargetLocation(action);
                    return;
                case "open-context-menu":
                    GameActions.OpenPopupMenu(RequireResolvedSerial(Target(action)), shift: true);
                    CompleteAction(action);
                    return;
                case "select-context-entry":
                    if (UIManager.PopupMenu != null)
                    {
                        BrQaTarget target = Target(action);
                        uint serial = RequireResolvedSerial(target);
                        int entry = action.Entry ?? target.ContextEntry ??
                            throw new InvalidDataException("Context target does not provide an entry.");
                        GameActions.ResponsePopupMenu(serial, checked((ushort)entry));
                        _emitter.EmitContextMenuResponseSent(action.Target, entry);
                        CompleteAction(action);
                    }
                    return;
                case "wait-container":
                    if (TryResolveSerial(Target(action), out uint containerSerial) && ContainerIsOpen(containerSerial))
                    {
                        _emitter.EmitContainerOpened(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "wait-item":
                    if (TryResolveSerial(Target(action), out uint itemSerial) && World.Instance?.Items.Get(itemSerial) != null)
                    {
                        _emitter.EmitItemObserved(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "wait-mobile":
                    if (TryResolveSerial(Target(action), out uint mobileSerial) && World.Instance?.Mobiles.Get(mobileSerial) != null)
                    {
                        _emitter.EmitMobileObserved(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "wait-property":
                    WaitProperty(action);
                    return;
                case "wait-status":
                    if (string.Equals(StatusValue(action), action.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _emitter.EmitStatusObserved(action.Field);
                        CompleteAction(action);
                    }
                    return;
                case "wait-combat-delta":
                    WaitCombatDelta(action);
                    return;
                case "wait-vendor-gump":
                    if (VendorGumpIsOpen(RequireResolvedSerial(Target(action))))
                    {
                        _emitter.EmitVendorGumpOpened(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "wait-trade-window":
                    if (UIManager.GetTradingGump(RequireResolvedSerial(Target(action))) != null)
                    {
                        _emitter.EmitTradeWindowOpened(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "gump-set-text":
                    _gumpEntries[checked((ushort)action.EntryId.Value)] = action.Text;
                    CompleteAction(action);
                    return;
                case "gump-select-switch":
                case "gump-select-radio":
                    AddSwitch(checked((uint)action.SwitchId.Value));
                    CompleteAction(action);
                    return;
                case "gump-select-list-entry":
                    AddSwitch(checked((uint)action.Entry.Value));
                    CompleteAction(action);
                    return;
                case "gump-click-button":
                    ReplyGump(action);
                    return;
                case "drag-item":
                    if (World.Instance?.Player != null && GameActions.PickUp(
                            World.Instance,
                            RequireResolvedSerial(Target(action)),
                            0,
                            0,
                            action.Amount.Value,
                            skipQueue: true
                        ))
                    {
                        _emitter.EmitItemDragged(action.Target, action.Amount.Value);
                        CompleteAction(action);
                    }
                    return;
                case "drop-item":
                    DropItem(action);
                    return;
                case "vendor-buy":
                case "vendor-sell":
                    VendorTransaction(action);
                    return;
                case "accept-trade":
                    GameActions.AcceptTrade(RequireResolvedSerial(Target(action)), action.Accepted.Value);
                    _emitter.EmitTradeResponseSent(action.Accepted.Value);
                    CompleteAction(action);
                    return;
                case "wait-duration":
                    _delayUntil = _delayUntil == default
                        ? DateTimeOffset.UtcNow.AddMilliseconds(action.DurationMs.Value)
                        : _delayUntil;
                    if (DateTimeOffset.UtcNow >= _delayUntil)
                        CompleteAction(action);
                    return;
                case "wait-skill":
                    if (World.Instance?.Player?.Skills[action.SkillIndex.Value].Value >= action.Minimum.Value)
                    {
                        _emitter.EmitSkillThresholdObserved(action.SkillIndex.Value, action.Minimum.Value);
                        CompleteAction(action);
                    }
                    return;
                case "wait-house-customization":
                    if (World.Instance?.CustomHouseManager?.Serial == RequireResolvedSerial(Target(action)))
                    {
                        _emitter.EmitHouseCustomizationEntered(action.Target);
                        CompleteAction(action);
                    }
                    return;
                case "wait-house-customization-exited":
                    if (World.Instance?.CustomHouseManager == null)
                        CompleteAction(action);
                    return;
                case "house-add-component":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseAddItem(
                        World.Instance,
                        checked((ushort)action.Graphic.Value),
                        action.X.Value,
                        action.Y.Value
                    );
                    _emitter.EmitHouseComponentAdded(action.Graphic.Value, action.X.Value, action.Y.Value);
                    CompleteAction(action);
                    return;
                case "house-remove-component":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseDeleteItem(
                        World.Instance,
                        checked((ushort)action.Graphic.Value),
                        action.X.Value,
                        action.Y.Value,
                        action.Z.Value
                    );
                    _emitter.EmitHouseComponentRemoved(action.Graphic.Value, action.X.Value, action.Y.Value, action.Z.Value);
                    CompleteAction(action);
                    return;
                case "house-backup":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseBackup(World.Instance);
                    _emitter.EmitHouseCustomizationOperation("backup");
                    CompleteAction(action);
                    return;
                case "house-restore":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseRestore(World.Instance);
                    _emitter.EmitHouseCustomizationOperation("restore");
                    CompleteAction(action);
                    return;
                case "house-revert":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseRevert(World.Instance);
                    _emitter.EmitHouseCustomizationOperation("revert");
                    CompleteAction(action);
                    return;
                case "house-commit":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseCommit(World.Instance);
                    _emitter.EmitHouseCustomizationCommitted();
                    CompleteAction(action);
                    return;
                case "house-exit":
                    RequireHouseCustomization();
                    AsyncNetClient.Socket.Send_CustomHouseBuildingExit(World.Instance);
                    _emitter.EmitHouseCustomizationOperation("exit");
                    CompleteAction(action);
                    return;
                case "logout":
                    Logout(action);
                    return;
                case "exit":
                    CompleteScenario();
                    return;
                default:
                    throw new InvalidOperationException("Unknown validated Beyond Recall QA action.");
            }
        }

        private BrQaTarget Target(BrQaAction action) => _targets.Require(action.Target);

        private static uint RequireResolvedSerial(BrQaTarget target)
        {
            if (!TryResolveSerial(target, out uint serial))
                throw new InvalidDataException("Beyond Recall QA target could not be resolved to a visible entity.");
            return serial;
        }

        private static bool TryResolveSerial(BrQaTarget target, out uint serial)
        {
            serial = 0;
            if (target?.Serial is > 0)
            {
                serial = target.Serial.Value;
                return true;
            }
            if (target?.Graphic == null || World.Instance == null)
                return false;

            Item best = null;
            uint backpack = World.Instance.Player?.Backpack?.Serial ?? 0;
            foreach (Item item in World.Instance.Items.Values)
            {
                bool graphicMatches = item.Graphic == target.Graphic || item.MultiGraphic == target.Graphic;
                if (!graphicMatches || target.X != null && item.X != target.X ||
                    target.Y != null && item.Y != target.Y || target.Z != null && item.Z != target.Z)
                {
                    continue;
                }

                bool inBackpack = backpack != 0 &&
                    (item.Container == backpack || item.BackpackOrRootContainer == backpack);
                bool bestInBackpack = best != null && backpack != 0 &&
                    (best.Container == backpack || best.BackpackOrRootContainer == backpack);
                if (best == null || inBackpack && !bestInBackpack ||
                    inBackpack == bestInBackpack && item.Serial > best.Serial)
                {
                    best = item;
                }
            }
            if (best == null)
                return false;

            target.Serial = best.Serial;
            serial = best.Serial;
            return true;
        }

        private static void RequireHouseCustomization()
        {
            if (World.Instance?.Player == null || World.Instance.CustomHouseManager == null)
                throw new InvalidOperationException("No active stock house-customization session exists.");
        }

        private void IssueSpeech(BrQaAction action, string message)
        {
            if (_actionIssued)
                return;

            _expectedJournal = FindNextJournalExpectation();
            _journalObserved = false;
            GameActions.Say(message);
            _actionIssued = true;
            CompleteAction(action);
        }

        internal static string BuildCheckpointCommand(string sessionToken, string target) =>
            $"[BrQaScenario {sessionToken} {target}";

        private void IssueEntityAction(BrQaAction action, Action<World, uint> operation)
        {
            if (World.Instance?.Player == null)
                return;

            operation(World.Instance, RequireResolvedSerial(Target(action)));
            CompleteAction(action);
        }

        private void Attack(BrQaAction action)
        {
            uint serial = RequireResolvedSerial(Target(action));
            Entity entity = World.Instance?.Get(serial);
            if (World.Instance?.Player == null || entity == null)
                return;

            _combatBaselines[action.Target] = entity.Hits;
            GameActions.Attack(World.Instance, serial);
            _emitter.EmitCombatActionSent(action.Target);
            CompleteAction(action);
        }

        private void TargetSerial(BrQaAction action)
        {
            if (World.Instance?.TargetManager?.IsTargeting != true)
                return;

            World.Instance.TargetManager.Target(RequireResolvedSerial(Target(action)));
            _emitter.EmitTargetSent(action.Target);
            CompleteAction(action);
        }

        private void TargetLocation(BrQaAction action)
        {
            if (World.Instance?.TargetManager?.IsTargeting != true)
                return;

            BrQaTarget target = Target(action);
            if (target.X == null || target.Y == null || target.Z == null)
                throw new InvalidDataException("Target location alias has no complete coordinates.");

            World.Instance.TargetManager.Target(
                target.Graphic ?? 0,
                target.X.Value,
                target.Y.Value,
                target.Z.Value
            );
            _emitter.EmitTargetSent(action.Target);
            CompleteAction(action);
        }

        private void WaitProperty(BrQaAction action)
        {
            uint serial = RequireResolvedSerial(Target(action));
            var opl = World.Instance?.OPL;
            if (opl == null)
                return;

            if (!opl.TryGetNameAndData(serial, out string name, out string data))
            {
                opl.Contains(serial);
                return;
            }

            string combined = (name ?? string.Empty) + "\n" + (data ?? string.Empty);
            if (combined.Contains(action.Contains, StringComparison.OrdinalIgnoreCase))
            {
                _emitter.EmitPropertyObserved(action.Target);
                CompleteAction(action);
            }
        }

        private string StatusValue(BrQaAction action)
        {
            var world = World.Instance;
            Entity entity = action.Target == null ? world?.Player : world?.Get(RequireResolvedSerial(Target(action)));
            if (entity == null)
                return null;

            return action.Field switch
            {
                "hits" => entity.Hits.ToString(CultureInfo.InvariantCulture),
                "hits-max" => entity.HitsMax.ToString(CultureInfo.InvariantCulture),
                "dead" when entity is Mobile mobile => mobile.IsDead.ToString().ToLowerInvariant(),
                "mana" when entity is Mobile mobile => mobile.Mana.ToString(CultureInfo.InvariantCulture),
                "stamina" when entity is Mobile mobile => mobile.Stamina.ToString(CultureInfo.InvariantCulture),
                "gold" when entity is PlayerMobile player => player.Gold.ToString(CultureInfo.InvariantCulture),
                "x" => entity.X.ToString(CultureInfo.InvariantCulture),
                "y" => entity.Y.ToString(CultureInfo.InvariantCulture),
                "z" => entity.Z.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException("Unsupported bounded QA status field.")
            };
        }

        private void WaitCombatDelta(BrQaAction action)
        {
            uint serial = RequireResolvedSerial(Target(action));
            Entity entity = World.Instance?.Get(serial);
            if (entity == null)
            {
                _emitter.EmitCombatDeltaObserved(action.Target);
                CompleteAction(action);
                return;
            }

            if (!_combatBaselines.TryGetValue(action.Target, out ushort baseline))
            {
                _combatBaselines[action.Target] = entity.Hits;
                GameActions.RequestMobileStatus(World.Instance, serial, force: true);
                return;
            }

            if (entity.Hits != baseline)
            {
                _emitter.EmitCombatDeltaObserved(action.Target);
                CompleteAction(action);
            }
        }

        private bool HasExpectedGump(BrQaAction action)
        {
            uint? expected = action.GumpId;
            if (action.Target != null)
                expected ??= Target(action).GumpId;

            if (expected != null)
                return UIManager.GetGumpServer(expected.Value) != null;

            return _gumpObservations > _gumpBaseline;
        }

        private Gump ResolveGump(BrQaAction action)
        {
            BrQaTarget target = action.Target == null ? null : Target(action);
            uint? expected = action.GumpId ?? target?.GumpId ?? _lastGumpId;
            Gump gump = expected == null ? null : UIManager.GetGumpServer(expected.Value);
            if (gump == null && target != null && TryResolveSerial(target, out uint serial))
                gump = UIManager.GetGump(serial);
            return gump;
        }

        private void ReplyGump(BrQaAction action)
        {
            Gump gump = ResolveGump(action);
            if (gump == null)
                return;

            var entries = _gumpEntries
                .OrderBy(pair => pair.Key)
                .Select(pair => Tuple.Create(pair.Key, pair.Value))
                .ToArray();
            int buttonId = action.ButtonId ??
                (action.Target == null ? null : Target(action).ButtonId) ??
                throw new InvalidDataException("Gump target does not provide a button id.");
            GameActions.ReplyGump(
                World.Instance,
                gump.LocalSerial,
                gump.ServerSerial,
                buttonId,
                _gumpSwitches.ToArray(),
                entries
            );
            _emitter.EmitGumpResponseSent(gump.ServerSerial, buttonId);
            _gumpSwitches.Clear();
            _gumpEntries.Clear();
            CompleteAction(action);
        }

        private void AddSwitch(uint value)
        {
            if (!_gumpSwitches.Contains(value))
                _gumpSwitches.Add(value);
        }

        private static bool ContainerIsOpen(uint serial) =>
            UIManager.GetGump<ContainerGump>(serial) != null ||
            UIManager.GetGump<GridContainer>(serial) != null;

        private static bool VendorGumpIsOpen(uint serial) =>
            UIManager.GetGump<ShopGump>(serial) != null ||
            UIManager.GetGump<ModernShopGump>(serial) != null;

        private void DropItem(BrQaAction action)
        {
            BrQaTarget item = Target(action);
            uint container = action.ContainerTarget == null
                ? 0xFFFF_FFFF
                : RequireResolvedSerial(_targets.Require(action.ContainerTarget));
            int x = action.X ?? 0xFFFF;
            int y = action.Y ?? 0xFFFF;
            int z = action.Z ?? 0;
            GameActions.DropItem(RequireResolvedSerial(item), x, y, z, container);
            _emitter.EmitItemDropped(action.Target, action.ContainerTarget);
            CompleteAction(action);
        }

        private void VendorTransaction(BrQaAction action)
        {
            uint vendor = RequireResolvedSerial(_targets.Require(action.ContainerTarget));
            uint item = RequireResolvedSerial(Target(action));
            var entries = new[] { Tuple.Create(item, checked((ushort)action.Amount.Value)) };
            if (action.Type == "vendor-buy")
            {
                AsyncNetClient.Socket.Send_BuyRequest(vendor, entries);
                _emitter.EmitVendorBuyResponseSent(action.ContainerTarget, action.Target, action.Amount.Value);
            }
            else
            {
                AsyncNetClient.Socket.Send_SellRequest(vendor, entries);
                _emitter.EmitVendorSellResponseSent(action.ContainerTarget, action.Target, action.Amount.Value);
            }
            CompleteAction(action);
        }

        private void Authenticate(BrQaAction action)
        {
            if (!_actionIssued)
            {
                var login = LoginScene.Instance;
                if (login == null)
                    return;

                var secret = BrQaSecret.Read(_config.SecretFilePath);
                login.Connect(secret.Username, secret.Password);
                _actionIssued = true;
            }

            if (!_accountAuthenticated || _serverSelected)
                return;

            var servers = LoginHandshake.Instance.Servers;
            if (servers == null || servers.Length != 1)
            {
                FailScenario("unexpected-server-list");
                return;
            }

            LoginScene.Instance?.SelectServer((byte)servers[0].Index);
            _serverSelected = true;
            CompleteAction(action);
        }

        private void SelectQaCharacter(BrQaAction action)
        {
            if (_actionIssued)
                return;

            var login = LoginScene.Instance;
            var characters = LoginHandshake.Instance.Characters;
            if (login == null || characters == null)
                return;

            var index = Array.FindIndex(
                characters,
                name => string.Equals(name, action.CharacterName, StringComparison.Ordinal)
            );
            if (index < 0)
            {
                FailScenario("qa-character-absent");
                return;
            }

            login.SelectCharacter((uint)index);
            _actionIssued = true;
            CompleteAction(action);
        }

        private void Walk(BrQaAction action, string direction, int steps)
        {
            if (!_actionIssued)
            {
                _movementBaseline = _movementAcknowledgements;
                _movementIssued = 0;
                _actionIssued = true;
            }

            var acknowledged = _movementAcknowledgements - _movementBaseline;
            if (acknowledged >= steps)
            {
                CompleteAction(action);
                return;
            }
            if (_movementIssued > acknowledged || _movementIssued >= steps || World.Instance?.Player == null)
                return;

            if (World.Instance.Player.Walk(ParseDirection(direction), false))
                _movementIssued++;
        }

        private void WalkPath(BrQaAction action)
        {
            List<BrQaPathStep> path = Target(action).Path;
            if (path.Count == 0)
                throw new InvalidDataException("walk-path target has no path steps.");
            if (_pathStepIndex >= path.Count)
            {
                CompleteAction(action);
                return;
            }

            BrQaPathStep step = path[_pathStepIndex];
            if (!_actionIssued)
            {
                _movementBaseline = _movementAcknowledgements;
                _movementIssued = 0;
                _actionIssued = true;
            }
            int acknowledged = _movementAcknowledgements - _movementBaseline;
            if (acknowledged >= step.Steps)
            {
                _pathStepIndex++;
                _actionIssued = false;
                if (_pathStepIndex >= path.Count)
                    CompleteAction(action);
                return;
            }
            if (_movementIssued > acknowledged || _movementIssued >= step.Steps || World.Instance?.Player == null)
                return;
            if (World.Instance.Player.Walk(ParseDirection(step.Direction), false))
                _movementIssued++;
        }

        private static Direction ParseDirection(string direction) => direction.ToLowerInvariant() switch
        {
            "north" => Direction.North,
            "northeast" => Direction.Right,
            "east" => Direction.East,
            "southeast" => Direction.Down,
            "south" => Direction.South,
            "southwest" => Direction.Left,
            "west" => Direction.West,
            "northwest" => Direction.Up,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        private string FindNextJournalExpectation()
        {
            if (_actionIndex + 1 < _plan.Actions.Count &&
                _plan.Actions[_actionIndex + 1].Type is "wait-journal" or "wait-system-message")
            {
                return _plan.Actions[_actionIndex + 1].Contains;
            }
            return null;
        }

        private void Logout(BrQaAction action)
        {
            if (!_actionIssued)
            {
                if (World.Instance?.Player == null)
                    return;
                GameActions.Logout(World.Instance);
                _actionIssued = true;
            }
            if (Client.Game?.Scene is LoginScene)
                CompleteAction(action);
        }

        private void CompleteAction(BrQaAction action)
        {
            _emitter.EmitActionCompleted(_actionIndex, action.Type, action.Target);
            _actionIndex++;
            _actionIssued = false;
            _actionDeadline = default;
            _pathStepIndex = 0;
            _movementIssued = 0;
            _gumpBaseline = _gumpObservations;
            _delayUntil = default;
        }

        private void CompleteScenario()
        {
            _scenarioCompleted = true;
            _emitter.EmitScenarioCompleted(_plan.Name);
            if (_config.ExitOnComplete)
            {
                _emitter.EmitClientExiting();
                Client.Game?.Exit();
            }
        }

        private void FailScenario(string code)
        {
            if (_scenarioFailed || _scenarioCompleted)
                return;

            _scenarioFailed = true;
            var actionType = _actionIndex < _plan.Actions.Count ? _plan.Actions[_actionIndex].Type : null;
            _emitter.EmitScenarioActionFailed(code, _actionIndex, actionType);
            _emitter.EmitScenarioFailed(code, _actionIndex, actionType);
            if (_config.ExitOnComplete)
            {
                _emitter.EmitClientExiting();
                Client.Game?.Exit();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (!_scenarioCompleted && !_scenarioFailed)
                FailScenario("unexpected-client-exit");
            _emitter.EmitClientExited();
            _disposed = true;
            _emitter.Dispose();
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            if (_instance == this)
                _instance = null;
        }
    }
}
