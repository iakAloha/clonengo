using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace clonengo
{
    public static class ProfileService
    {
        private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        /// <summary>Palauttaa koneen oikeat käyttäjäprofiilit (ei järjestelmäprofiileja).</summary>
        public static List<(string Name, string Sid, string Path)> GetProfiles()
        {
            var result = new List<(string, string, string)>();
            using var root = Registry.LocalMachine.OpenSubKey(ProfileListKey);
            if (root is null) return result;

            foreach (var sid in root.GetSubKeyNames())
            {
                // Vain paikalliset/verkkotunnuksen/AAD-käyttäjät (S-1-5-21-... ja S-1-12-1-...)
                if (!sid.StartsWith("S-1-5-21-") && !sid.StartsWith("S-1-12-1-")) continue;

                using var key = root.OpenSubKey(sid);
                var path = key?.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrEmpty(path)) continue;
                path = Environment.ExpandEnvironmentVariables(path);
                if (!Directory.Exists(path)) continue;

                result.Add((System.IO.Path.GetFileName(path), sid, path));
            }
            result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Laskee kansion koon rekursiivisesti. Ohittaa linkit ja kansiot, joihin ei ole oikeutta.</summary>
        public static long GetDirectorySize(string path, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = false,
            };

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                    {
                        if (entry is FileInfo f) total += f.Length;
                        else stack.Push(entry.FullName);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            return total;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "t", "kt", "Mt", "Gt", "Tt" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{bytes} t" : $"{v:0.#} {units[i]}";
        }
    }
}
