using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace vault.UI
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string? startupVaultPath = ResolveStartupVaultPath(e.Args);
            var mainWindow = new MainWindow(startupVaultPath);
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        private static string? ResolveStartupVaultPath(string[] args)
        {
            var allArgs = new List<string>();
            if (args != null && args.Length > 0)
                allArgs.AddRange(args);

            string[] cmdLineArgs = Environment.GetCommandLineArgs();
            if (cmdLineArgs.Length > 1)
                allArgs.AddRange(cmdLineArgs.Skip(1));

            if (allArgs.Count == 0)
                return null;

            bool nextArgIsVaultPath = false;
            foreach (string rawArg in allArgs)
            {
                if (string.IsNullOrWhiteSpace(rawArg))
                    continue;

                string arg = rawArg.Trim();

                if (nextArgIsVaultPath)
                {
                    string? fromNext = NormalizeVaultPathCandidate(arg);
                    if (!string.IsNullOrWhiteSpace(fromNext))
                        return fromNext;

                    nextArgIsVaultPath = false;
                    continue;
                }

                if (IsVaultFlag(arg))
                {
                    nextArgIsVaultPath = true;
                    continue;
                }

                if (TrySplitKeyValueArgument(arg, out string key, out string value) &&
                    IsVaultFlag(key))
                {
                    string? fromValue = NormalizeVaultPathCandidate(value);
                    if (!string.IsNullOrWhiteSpace(fromValue))
                        return fromValue;

                    continue;
                }

                string? directPath = NormalizeVaultPathCandidate(arg);
                if (!string.IsNullOrWhiteSpace(directPath))
                    return directPath;
            }

            return null;
        }

        private static bool IsVaultFlag(string arg) =>
            arg.Equals("--vault", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("/vault", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-vault", StringComparison.OrdinalIgnoreCase);

        private static bool TrySplitKeyValueArgument(string arg, out string key, out string value)
        {
            int equalsIndex = arg.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex >= arg.Length - 1)
            {
                key = string.Empty;
                value = string.Empty;
                return false;
            }

            key = arg[..equalsIndex].Trim();
            value = arg[(equalsIndex + 1)..].Trim();
            return true;
        }

        private static string? NormalizeVaultPathCandidate(string rawCandidate)
        {
            if (string.IsNullOrWhiteSpace(rawCandidate))
                return null;

            string candidate = rawCandidate.Trim().Trim('"');
            if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out Uri? fileUri) &&
                fileUri.IsFile)
            {
                candidate = fileUri.LocalPath;
            }

            if (!candidate.EndsWith(".vault", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                return Path.GetFullPath(candidate);
            }
            catch
            {
                return candidate;
            }
        }
    }
}
