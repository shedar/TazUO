using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassicUO.BeyondRecallQA
{
    internal sealed class BrQaConfig
    {
        private const string Prefix = "-br-qa-";

        public bool Enabled { get; private set; }
        public string PlanPath { get; private set; }
        public string OutputDirectory { get; private set; }
        public string SessionToken { get; private set; }
        public string SecretFilePath { get; private set; }
        public string SecretsRoot { get; private set; }
        public string DataIdentity { get; private set; }
        public string PreparationBuildIdentity { get; private set; }
        public string ServerEndpoint { get; private set; }
        public string ClientVersion { get; private set; }
        public bool ExitOnComplete { get; private set; }

        public static BrQaConfig Disabled { get; } = new BrQaConfig();

        public static BrQaConfig Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            var config = new BrQaConfig();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var qaRequested = false;

            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (!option.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                qaRequested = true;
                if (!seen.Add(option))
                    throw new ArgumentException("Duplicate Beyond Recall QA option: " + option);

                switch (option.ToLowerInvariant())
                {
                    case "-br-qa-plan":
                        config.PlanPath = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-output":
                        config.OutputDirectory = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-session":
                        config.SessionToken = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-secret-file":
                        config.SecretFilePath = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-secrets-root":
                        config.SecretsRoot = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-data-identity":
                        config.DataIdentity = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-preparation-build-identity":
                        config.PreparationBuildIdentity = RequireValue(args, ref i, option);
                        break;
                    case "-br-qa-exit-on-complete":
                        config.ExitOnComplete = true;
                        break;
                    default:
                        throw new ArgumentException("Unknown Beyond Recall QA option: " + option);
                }
            }

            if (!qaRequested)
                return Disabled;

            Require(config.PlanPath, "-br-qa-plan");
            Require(config.OutputDirectory, "-br-qa-output");
            Require(config.SessionToken, "-br-qa-session");
            Require(config.SecretFilePath, "-br-qa-secret-file");
            Require(config.SecretsRoot, "-br-qa-secrets-root");
            Require(config.DataIdentity, "-br-qa-data-identity");
            Require(config.PreparationBuildIdentity, "-br-qa-preparation-build-identity");

            if (!IsLowerHex(config.SessionToken, 32))
                throw new ArgumentException("Beyond Recall QA session token must be exactly 32 lowercase hexadecimal characters.");
            if (!IsLowerHex(config.DataIdentity, 64))
                throw new ArgumentException("Beyond Recall QA data identity must be exactly 64 lowercase hexadecimal characters.");
            if (!IsLowerHex(config.PreparationBuildIdentity, 64))
                throw new ArgumentException("Beyond Recall QA preparation build identity must be exactly 64 lowercase hexadecimal characters.");

            var ip = RequireOrdinaryValue(args, "-ip");
            var port = RequireOrdinaryValue(args, "-port");
            config.ClientVersion = RequireOrdinaryValue(args, "-clientversion");
            if (!string.Equals(ip, "127.0.0.1", StringComparison.Ordinal) ||
                !ushort.TryParse(port, out var parsedPort) || parsedPort == 0)
            {
                throw new ArgumentException("Beyond Recall QA mode requires an explicit loopback server endpoint.");
            }
            if (config.ClientVersion.Length > 32 ||
                config.ClientVersion.Any(c => !(char.IsAsciiDigit(c) || c == '.')))
            {
                throw new ArgumentException("Beyond Recall QA client version is invalid.");
            }

            config.ServerEndpoint = ip + ":" + parsedPort;

            ValidatePaths(config);
            config.Enabled = true;
            return config;
        }

        private static string RequireOrdinaryValue(string[] args, string option)
        {
            string value = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (value != null)
                    throw new ArgumentException("Duplicate Beyond Recall QA launch option: " + option);
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) ||
                    args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Beyond Recall QA launch option requires a non-option value: " + option);
                }

                value = args[++i];
            }

            return value ?? throw new ArgumentException("Beyond Recall QA mode requires launch option " + option + ".");
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException("Beyond Recall QA option requires a non-option value: " + option);
            }

            return args[++index];
        }

        private static void Require(string value, string option)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Beyond Recall QA mode requires " + option + ".");
        }

        private static bool IsLowerHex(string value, int length) =>
            value != null && value.Length == length &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static void ValidatePaths(BrQaConfig config)
        {
            config.PlanPath = ValidateAbsolute(config.PlanPath, "plan");
            config.OutputDirectory = ValidateAbsolute(config.OutputDirectory, "output directory");
            config.SecretFilePath = ValidateAbsolute(config.SecretFilePath, "secret file");
            config.SecretsRoot = ValidateAbsolute(config.SecretsRoot, "secrets root");

            ValidatePathComponents(config.OutputDirectory, requireLeaf: true, expectDirectory: true, "output directory");
            ValidatePathComponents(config.PlanPath, requireLeaf: true, expectDirectory: false, "plan");
            ValidatePathComponents(config.SecretsRoot, requireLeaf: true, expectDirectory: true, "secrets root");
            ValidatePathComponents(config.SecretFilePath, requireLeaf: true, expectDirectory: false, "secret file");

            if (!PathEquals(Path.GetDirectoryName(config.PlanPath), config.OutputDirectory))
                throw new ArgumentException("Beyond Recall QA plan must be a direct child of the output directory.");
            if (!IsStrictDescendant(config.SecretsRoot, config.SecretFilePath))
                throw new ArgumentException("Beyond Recall QA secret file must be beneath the declared external secrets root.");
            if (File.Exists(Path.Combine(config.OutputDirectory, "qa-events.jsonl")))
                throw new ArgumentException("Beyond Recall QA output already contains an event stream and will not be overwritten.");

            ValidateSecretMode(config.SecretFilePath);
        }

        private static string ValidateAbsolute(string path, string label)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("Beyond Recall QA " + label + " path must be absolute.");

            var components = path.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries
            );
            if (components.Any(component => component == ".."))
                throw new ArgumentException("Beyond Recall QA " + label + " path contains parent traversal.");

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        private static void ValidatePathComponents(
            string path,
            bool requireLeaf,
            bool expectDirectory,
            string label
        )
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("Beyond Recall QA " + label + " path has no filesystem root.");

            var current = Path.TrimEndingDirectorySeparator(root);
            var remainder = fullPath[root.Length..];
            var components = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries
            );
            for (var i = 0; i < components.Length; i++)
            {
                current = Path.Combine(current.Length == 0 ? root : current, components[i]);
                var directory = new DirectoryInfo(current);
                var file = new FileInfo(current);
                var exists = directory.Exists || file.Exists;
                if (!exists)
                {
                    if (requireLeaf || i != components.Length - 1)
                        throw new ArgumentException("Beyond Recall QA " + label + " path does not exist.");
                    break;
                }

                var attributes = directory.Exists ? directory.Attributes : file.Attributes;
                if (directory.LinkTarget != null || file.LinkTarget != null || attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Beyond Recall QA " + label + " path contains a symbolic link/reparse point.");
                if (file.Exists && i < components.Length - 1)
                    throw new ArgumentException("Beyond Recall QA " + label + " path has a file where a directory is required.");
            }

            if (expectDirectory && !Directory.Exists(path))
                throw new ArgumentException("Beyond Recall QA " + label + " must be an existing directory.");
            if (!expectDirectory && !File.Exists(path))
                throw new ArgumentException("Beyond Recall QA " + label + " must be an existing regular file.");
        }

        private static void ValidateSecretMode(string path)
        {
            if (OperatingSystem.IsWindows())
                return;

            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            UnixFileMode actual;
            try
            {
                actual = File.GetUnixFileMode(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException("Beyond Recall QA secret-file permissions could not be validated.", exception);
            }

            if (actual != expected)
                throw new ArgumentException("Beyond Recall QA secret file must have Unix mode 0600.");
        }

        private static bool IsStrictDescendant(string root, string candidate)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(
                prefix,
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            );
        }

        private static bool PathEquals(string left, string right) => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );
    }
}
