// SPDX-License-Identifier: BSD-2-Clause
// Beyond Recall QA evidence mode - opt-in, local-only, no credentials in events.

using System;
using System.IO;

namespace ClassicUO.BeyondRecallQA
{
    internal sealed class BrQaConfig
    {
        public bool Enabled { get; private set; }
        public string PlanPath { get; private set; }
        public string OutputDirectory { get; private set; }
        public string SessionToken { get; private set; }
        public string SecretFilePath { get; private set; }
        public bool ExitOnComplete { get; private set; }

        public static BrQaConfig Disabled { get; } = new BrQaConfig();

        public static BrQaConfig Parse(string[] args)
        {
            var config = new BrQaConfig();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();

                switch (arg)
                {
                    case "-br-qa-plan":
                        if (i + 1 < args.Length) config.PlanPath = args[++i];
                        break;
                    case "-br-qa-output":
                        if (i + 1 < args.Length) config.OutputDirectory = args[++i];
                        break;
                    case "-br-qa-session":
                        if (i + 1 < args.Length) config.SessionToken = args[++i];
                        break;
                    case "-br-qa-secret-file":
                        if (i + 1 < args.Length) config.SecretFilePath = args[++i];
                        break;
                    case "-br-qa-exit-on-complete":
                        config.ExitOnComplete = true;
                        break;
                }
            }

            config.Enabled = config.PlanPath != null &&
                             config.OutputDirectory != null &&
                             config.SessionToken != null;

            if (config.Enabled)
                ValidatePaths(config);

            return config;
        }

        private static void ValidatePaths(BrQaConfig config)
        {
            if (!Path.IsPathFullyQualified(config.PlanPath))
                throw new ArgumentException("BR QA plan path must be absolute: " + config.PlanPath);
            if (!Path.IsPathFullyQualified(config.OutputDirectory))
                throw new ArgumentException("BR QA output directory must be absolute: " + config.OutputDirectory);
            if (config.SecretFilePath != null && !Path.IsPathFullyQualified(config.SecretFilePath))
                throw new ArgumentException("BR QA secret file path must be absolute: " + config.SecretFilePath);

            RejectSymlink(config.PlanPath, "plan");
            RejectSymlink(config.OutputDirectory, "output directory");
            if (config.SecretFilePath != null)
                RejectSymlink(config.SecretFilePath, "secret file");

            RejectTraversal(config.PlanPath, "plan");
            RejectTraversal(config.OutputDirectory, "output directory");
            if (config.SecretFilePath != null)
                RejectTraversal(config.SecretFilePath, "secret file");
        }

        private static void RejectSymlink(string path, string label)
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null)
                throw new ArgumentException("BR QA " + label + " path is a symlink and is rejected: " + path);
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                if (dirInfo.LinkTarget != null)
                    throw new ArgumentException("BR QA " + label + " path is a symlink and is rejected: " + path);
            }
        }

        private static void RejectTraversal(string path, string label)
        {
            var components = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
            {
                if (component == "..")
                    throw new ArgumentException("BR QA " + label + " path contains parent traversal: " + path);
            }
        }
    }
}
