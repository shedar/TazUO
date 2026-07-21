// SPDX-License-Identifier: BSD-2-Clause
// Beyond Recall QA evidence mode - main session coordinator.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Configuration;
using ClassicUO.Game;
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
        private readonly CancellationTokenSource _cts;
        private bool _disposed;
        private bool _scenarioCompleted;
        private string _failureReason;

        public BrQaConfig Config => _config;
        public BrQaEventEmitter Emitter => _emitter;
        public bool ScenarioCompleted => _scenarioCompleted;

        private BrQaSession(BrQaConfig config)
        {
            _config = config;
            _emitter = new BrQaEventEmitter(config.OutputDirectory, config.SessionToken);
            _plan = BrQaPlan.Load(config.PlanPath);
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(_plan.TimeoutSeconds));
        }

        public static BrQaSession Initialize(string[] args)
        {
            var config = BrQaConfig.Parse(args);

            if (!config.Enabled)
                return null;

            _instance = new BrQaSession(config);
            _instance._emitter.EmitProcessStarted();

            Log.Info("Beyond Recall QA session initialized: " + config.SessionToken);
            return _instance;
        }

        public void SetServerEndpoint(string ip, ushort port)
        {
            _emitter.ServerEndpoint = ip + ":" + port;
        }

        public void SetClientVersion(string version)
        {
            _emitter.ClientVersion = version;
        }

        public void SetDataIdentity(string identity)
        {
            _emitter.DataIdentity = identity;
        }

        public void EmitSettingsLoaded() => _emitter.EmitSettingsLoaded();
        public void EmitLegalDataOpened() => _emitter.EmitLegalDataOpened();
        public void EmitAssetsLoaded() => _emitter.EmitAssetsLoaded();
        public void EmitWindowReady() => _emitter.EmitWindowReady();
        public void EmitNetworkConnected(string endpoint) => _emitter.EmitNetworkConnected(endpoint);
        public void EmitServerListReceived(int count) => _emitter.EmitServerListReceived(count);
        public void EmitAccountAuthenticated() => _emitter.EmitAccountAuthenticated();
        public void EmitCharacterListReceived(int count) => _emitter.EmitCharacterListReceived(count);
        public void EmitCharacterEnteredWorld(string name) => _emitter.EmitCharacterEnteredWorld(name);
        public void EmitMovementAcknowledged(string direction) => _emitter.EmitMovementAcknowledged(direction);
        public void EmitGumpOpened(uint gumpId) => _emitter.EmitGumpOpened(gumpId);
        public void EmitJournalObserved(string text) => _emitter.EmitJournalObserved(text);

        public void CompleteScenario()
        {
            _scenarioCompleted = true;
            _emitter.EmitScenarioCompleted(_plan.Name);

            if (_config.ExitOnComplete)
            {
                _emitter.EmitClientExiting();
                Client.Game?.Exit();
            }
        }

        public void FailScenario(string reason)
        {
            _failureReason = reason;
            _emitter.EmitScenarioFailed(reason);

            if (_config.ExitOnComplete)
            {
                _emitter.EmitClientExiting();
                Client.Game?.Exit();
            }
        }

        public (string username, string password)? ReadSecrets()
        {
            if (string.IsNullOrEmpty(_config.SecretFilePath))
                return null;

            if (!File.Exists(_config.SecretFilePath))
            {
                FailScenario("Secret file not found: " + _config.SecretFilePath);
                return null;
            }

            try
            {
                var json = File.ReadAllText(_config.SecretFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var username = root.GetProperty("username").GetString();
                var password = root.GetProperty("password").GetString();

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    FailScenario("Secret file missing username or password.");
                    return null;
                }

                return (username, password);
            }
            catch (Exception ex)
            {
                FailScenario("Failed to read secret file: " + ex.Message);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _emitter.EmitClientExited();
            _emitter.Dispose();
            _cts.Dispose();

            if (_instance == this)
                _instance = null;
        }
    }
}
