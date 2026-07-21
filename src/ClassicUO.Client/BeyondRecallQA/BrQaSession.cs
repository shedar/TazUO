using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
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
        private readonly DateTimeOffset _deadline;
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
        private int _gumpObservations;
        private int _gumpBaseline;
        private uint? _lastGumpId;
        private string _expectedJournal;
        private bool _journalObserved;

        public BrQaConfig Config => _config;
        public bool ScenarioCompleted => _scenarioCompleted;
        internal BrQaEventEmitter Emitter => _emitter;

        private BrQaSession(BrQaConfig config)
        {
            _config = config;
            _plan = BrQaPlan.Load(config.PlanPath);
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
                _actionDeadline = DateTimeOffset.UtcNow.AddSeconds(action.TimeoutSeconds);
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
                exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException
            )
            {
                FailScenario("action-failed");
            }
        }

        private void ExecuteAction(BrQaAction action)
        {
            switch (action.Type)
            {
                case "connect":
                    CompleteAction();
                    return;
                case "authenticate-from-secret":
                    Authenticate();
                    return;
                case "select-or-create-named-qa-character":
                    SelectQaCharacter(action.CharacterName);
                    return;
                case "wait-enter-world":
                    if (_characterEnteredWorld && World.Instance?.InGame == true)
                        CompleteAction();
                    return;
                case "walk-relative":
                    Walk(action);
                    return;
                case "speak":
                    if (!_actionIssued)
                    {
                        _expectedJournal = FindNextJournalExpectation();
                        _journalObserved = false;
                        GameActions.Say(action.Message);
                        _actionIssued = true;
                        CompleteAction();
                    }
                    return;
                case "wait-journal":
                    _expectedJournal ??= action.Contains;
                    if (_journalObserved)
                        CompleteAction();
                    return;
                case "open-paperdoll":
                    if (World.Instance?.Player != null)
                    {
                        GameActions.OpenPaperdoll(World.Instance, World.Instance.Player.Serial);
                        CompleteAction();
                    }
                    return;
                case "open-backpack":
                    if (World.Instance?.Player != null && GameActions.OpenBackpack(World.Instance))
                        CompleteAction();
                    return;
                case "open-skills":
                    if (World.Instance?.Player != null)
                    {
                        GameActions.OpenSkills(World.Instance);
                        CompleteAction();
                    }
                    return;
                case "send-server-command":
                    if (!_actionIssued)
                    {
                        _gumpBaseline = _gumpObservations;
                        GameActions.Say(action.Command);
                        _actionIssued = true;
                        CompleteAction();
                    }
                    return;
                case "wait-gump":
                    if (_gumpObservations > _gumpBaseline &&
                        (action.GumpId == null || action.GumpId == _lastGumpId))
                    {
                        CompleteAction();
                    }
                    return;
                case "logout":
                    if (!_actionIssued)
                    {
                        if (World.Instance?.Player == null)
                            return;
                        GameActions.Logout(World.Instance);
                        _actionIssued = true;
                    }
                    if (Client.Game?.Scene is LoginScene)
                        CompleteAction();
                    return;
                case "exit":
                    CompleteScenario();
                    return;
                default:
                    throw new InvalidOperationException("Unknown validated Beyond Recall QA action.");
            }
        }

        private void Authenticate()
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
            CompleteAction();
        }

        private void SelectQaCharacter(string characterName)
        {
            if (_actionIssued)
                return;

            var login = LoginScene.Instance;
            var characters = LoginHandshake.Instance.Characters;
            if (login == null || characters == null)
                return;

            var index = Array.FindIndex(characters, name => string.Equals(name, characterName, StringComparison.Ordinal));
            if (index < 0)
            {
                FailScenario("qa-character-absent");
                return;
            }

            login.SelectCharacter((uint)index);
            _actionIssued = true;
            CompleteAction();
        }

        private void Walk(BrQaAction action)
        {
            if (!_actionIssued)
            {
                _movementBaseline = _movementAcknowledgements;
                _movementIssued = 0;
                _actionIssued = true;
            }

            var acknowledged = _movementAcknowledgements - _movementBaseline;
            if (acknowledged >= action.Steps)
            {
                CompleteAction();
                return;
            }
            if (_movementIssued > acknowledged || _movementIssued >= action.Steps || World.Instance?.Player == null)
                return;

            if (World.Instance.Player.Walk(ParseDirection(action.Direction), false))
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
            if (_actionIndex + 1 < _plan.Actions.Count && _plan.Actions[_actionIndex + 1].Type == "wait-journal")
                return _plan.Actions[_actionIndex + 1].Contains;
            return null;
        }

        private void CompleteAction()
        {
            _actionIndex++;
            _actionIssued = false;
            _actionDeadline = default;
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
