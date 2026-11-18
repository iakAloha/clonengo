using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace clonengo
{
    public enum CompressionMethod { None, Zip, SevenZip }

    public sealed record ProfileSource(string Name, string Sid, string Path);

    public sealed record ArchiveProgress(long BytesDone, string CurrentFile);

    public sealed record SkippedItem(string Path, string Reason);

    public sealed class ProfileResult
    {
        public string Name { get; set; } = "";
        public string Sid { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string ArchiveEntry { get; set; } = "";
        public int Files { get; set; }
        public long Bytes { get; set; }
        public int ExcludedFolders { get; set; }
        public List<SkippedItem> Skipped { get; set; } = new();
    }

    public sealed class ArchiveResult
    {
        public List<ProfileResult> Profiles { get; } = new();
        public int Files => Profiles.Sum(p => p.Files);
        public long Bytes => Profiles.Sum(p => p.Bytes);
        public int SkippedCount => Profiles.Sum(p => p.Skipped.Count);
        public long ArchiveSize { get; set; }
    }

    /// <summary>
    /// Kirjoittaa .clonengo-tiedoston: ulompi ZIP (ei pakkausta), jonka sisällä
    /// manifest.json ja yksi ZIP per profiili. Ei riippuvuuksia käyttöliittymään.
    /// </summary>
    public static class ProfileArchiver
    {
        public const string FormatName = "clonengo";
        public const int FormatVersion = 1;
        public const string ManifestName = "manifest.json";

        private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp3", ".aac", ".m4a", ".flac", ".ogg",
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst",
            ".docx", ".xlsx", ".pptx", ".odt", ".pdf", ".msi", ".cab", ".jar", ".nupkg",
        };

        private const int MaxSkippedInManifest = 1000;

        /// <summary>Mukana toimitettu 7-Zip (7zip-kansio ohjelman vieressä).</summary>
        public static string SevenZipPath => Path.Combine(AppContext.BaseDirectory, "7zip", "7z.exe");
        public static bool SevenZipAvailable => File.Exists(SevenZipPath);

        public static ArchiveResult Create(
            string outputPath,
            IReadOnlyList<ProfileSource> profiles,
            CompressionMethod method,
            IEnumerable<ExclusionOption> exclusions,
            IProgress<ArchiveProgress>? progress,
            CancellationToken ct)
        {
            var exclusionList = exclusions.ToList();
            var matcher = new ExclusionMatcher(exclusionList.Where(e => e.IsExcluded).SelectMany(e => e.Paths));
            var result = new ArchiveResult();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
            string fullOutput = Path.GetFullPath(outputPath);
            long done = 0;
            if (method == CompressionMethod.SevenZip && !SevenZipAvailable)
                throw new FileNotFoundException("7-Zip puuttuu: " + SevenZipPath);

            try
            {
                using (var fs = new FileStream(fullOutput, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var outer = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
                {
                    foreach (var profile in profiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        var pr = new ProfileResult { Name = profile.Name, Sid = profile.Sid, SourcePath = profile.Path };
                        string ext = method == CompressionMethod.SevenZip ? ".7z" : ".zip";
                        pr.ArchiveEntry = UniqueName(SafeFileName(profile.Name) + ext, usedNames);

                        var entry = outer.CreateEntry(pr.ArchiveEntry, CompressionLevel.NoCompression);
                        entry.LastWriteTime = DateTimeOffset.Now;
                        if (method == CompressionMethod.SevenZip)
                        {
                            AddProfile7z(entry, profile, pr, matcher, fullOutput, progress, ref done, ct);
                        }
                        else
                        {
                            using var es = entry.Open();
                            using var inner = new ZipArchive(es, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                            AddProfile(inner, profile, pr, matcher, method == CompressionMethod.Zip, fullOutput, progress, ref done, ct);
                        }
                        result.Profiles.Add(pr);
                    }

                    WriteManifest(outer, result, method, exclusionList);
                }
                result.ArchiveSize = new FileInfo(fullOutput).Length;
                progress?.Report(new ArchiveProgress(done, ""));
                return result;
            }
            catch
            {
                try { File.Delete(fullOutput); } catch { }
                throw;
            }
        }

        private static void AddProfile(ZipArchive zip, ProfileSource profile, ProfileResult pr,
            ExclusionMatcher matcher, bool compress, string outputFullPath,
            IProgress<ArchiveProgress>? progress, ref long done, CancellationToken ct)
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                // Oletus ohittaisi piilotetut ja järjestelmätiedostot (esim. NTUSER.DAT, AppData)
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = false,
            };

            var stack = new Stack<(string Full, string Rel)>();
            stack.Push((profile.Path, ""));
            var buffer = new byte[128 * 1024];
            long sinceReport = 0;

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, rel) = stack.Pop();

                if (rel.Length > 0)
                {
                    if (matcher.IsExcluded(rel)) { pr.ExcludedFolders++; continue; }
                    zip.CreateEntry(rel.Replace('\\', '/') + "/");
                }

                List<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumOptions).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pr.Skipped.Add(new SkippedItem(dir, ex.Message));
                    continue;
                }

                foreach (var info in entries)
                {
                    string childRel = rel.Length == 0 ? info.Name : rel + "\\" + info.Name;
                    if (info is DirectoryInfo)
                    {
                        stack.Push((info.FullName, childRel));
                        continue;
                    }
                    if (string.Equals(info.FullName, outputFullPath, StringComparison.OrdinalIgnoreCase))
                        continue; // älä pakkaa kohdetiedostoa itseensä

                    FileStream src;
                    try
                    {
                        src = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        pr.Skipped.Add(new SkippedItem(info.FullName, ex.Message));
                        continue;
                    }

                    using (src)
                    {
                        var level = compress && !AlreadyCompressed.Contains(Path.GetExtension(info.Name))
                            ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                        var ze = zip.CreateEntry(childRel.Replace('\\', '/'), level);
                        ze.LastWriteTime = ClampTime(info.LastWriteTime);

                        try
                        {
                            using var dst = ze.Open();
                            int n;
                            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                dst.Write(buffer, 0, n);
                                pr.Bytes += n;
                                done += n;
                                sinceReport += n;
                                if (sinceReport >= 4 * 1024 * 1024)
                                {
                                    sinceReport = 0;
                                    progress?.Report(new ArchiveProgress(done, info.FullName));
                                }
                            }
                            pr.Files++;
                        }
                        catch (IOException ex)
                        {
                            pr.Skipped.Add(new SkippedItem(info.FullName, "Luku keskeytyi, tiedosto voi olla osittainen: " + ex.Message));
                        }
                    }
                }
            }
        }

        /// <summary>Käy profiilin läpi: kansiot (File == null) ja tiedostot, pois jätetyt kansiot ohittaen.</summary>
        private static IEnumerable<(string Rel, FileInfo? File)> Walk(
            ProfileSource profile, ProfileResult pr, ExclusionMatcher matcher, string outputFullPath, CancellationToken ct)
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = false,
            };

            var stack = new Stack<(string Full, string Rel)>();
            stack.Push((profile.Path, ""));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, rel) = stack.Pop();

                if (rel.Length > 0)
                {
                    if (matcher.IsExcluded(rel)) { pr.ExcludedFolders++; continue; }
                    yield return (rel, null);
                }

                List<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumOptions).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pr.Skipped.Add(new SkippedItem(dir, ex.Message));
                    continue;
                }

                foreach (var info in entries)
                {
                    string childRel = rel.Length == 0 ? info.Name : rel + "\\" + info.Name;
                    if (info is DirectoryInfo)
                    {
                        stack.Push((info.FullName, childRel));
                        continue;
                    }
                    if (string.Equals(info.FullName, outputFullPath, StringComparison.OrdinalIgnoreCase))
                        continue; // älä pakkaa kohdetiedostoa itseensä
                    yield return (childRel, (FileInfo)info);
                }
            }
        }

        /// <summary>
        /// Pakkaa profiilin 7-Zipillä väliaikaiseen tiedostoon ja kopioi sen ulomman arkiston kohtaan.
        /// Tiedostolista tehdään itse, jotta pois jätetyt kansiot noudattavat samoja sääntöjä kuin ZIP-tilassa.
        /// </summary>
        private static void AddProfile7z(ZipArchiveEntry entry, ProfileSource profile, ProfileResult pr,
            ExclusionMatcher matcher, string outputFullPath, IProgress<ArchiveProgress>? progress,
            ref long done, CancellationToken ct)
        {
            string tempDir = Path.GetDirectoryName(outputFullPath) ?? Path.GetTempPath();
            string id = Guid.NewGuid().ToString("N");
            string listFile = Path.Combine(tempDir, id + ".list.tmp");
            string tempArchive = Path.Combine(tempDir, id + ".7z.tmp");
            long profileBytes = 0;

            try
            {
                using (var lw = new StreamWriter(listFile, false, new UTF8Encoding(true)))
                {
                    foreach (var (rel, file) in Walk(profile, pr, matcher, outputFullPath, ct))
                    {
                        lw.WriteLine(rel);
                        if (file != null) { pr.Files++; profileBytes += file.Length; }
                    }
                }
                pr.Bytes = profileBytes;

                var psi = new System.Diagnostics.ProcessStartInfo(SevenZipPath)
                {
                    WorkingDirectory = profile.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in new[] { "a", "-t7z", "-mx=5", "-mmt=on", "-ssw", "-spd", "-r-", "-y",
                                          "-bso0", "-bsp1", "-scsUTF-8", "-sccUTF-8", tempArchive, "@" + listFile })
                    psi.ArgumentList.Add(a);

                using var proc = System.Diagnostics.Process.Start(psi)
                    ?? throw new IOException("7-Zip ei käynnistynyt");
                using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

                var errors = new StringBuilder();
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                proc.BeginErrorReadLine();

                long baseDone = done;
                int lastPercent = -1;
                var token = new StringBuilder();
                int ch;
                while ((ch = proc.StandardOutput.Read()) >= 0)
                {
                    if (ch != '\r' && ch != '\n') { token.Append((char)ch); continue; }
                    var text = token.ToString().Trim();
                    token.Clear();
                    int pct = text.IndexOf('%');
                    if (pct > 0 && int.TryParse(text.AsSpan(0, pct), out int p) && p != lastPercent)
                    {
                        lastPercent = p;
                        done = baseDone + profileBytes * p / 100;
                        int plus = text.IndexOf("+ ", pct, StringComparison.Ordinal);
                        progress?.Report(new ArchiveProgress(done, plus >= 0 ? text[(plus + 2)..] : profile.Name));
                    }
                }
                proc.WaitForExit();
                ct.ThrowIfCancellationRequested();
                done = baseDone + profileBytes;

                string err = errors.ToString();
                if (proc.ExitCode == 1)
                {
                    // Varoitus: osa tiedostoista jäi pakkaamatta (lukitus tms.)
                    foreach (var line in err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        pr.Skipped.Add(new SkippedItem(line, "7-Zip-varoitus"));
                }
                else if (proc.ExitCode != 0)
                    throw new IOException($"7-Zip epäonnistui (koodi {proc.ExitCode}): {err.Trim()}");

                using var fsTemp = new FileStream(tempArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                using var dst = entry.Open();
                fsTemp.CopyTo(dst);
            }
            finally
            {
                try { File.Delete(listFile); } catch { }
                try { File.Delete(tempArchive); } catch { }
            }
        }

        private static void WriteManifest(ZipArchive outer, ArchiveResult result, CompressionMethod method, List<ExclusionOption> exclusions)
        {
            var manifest = new
            {
                format = FormatName,
                version = FormatVersion,
                created = DateTimeOffset.Now,
                machine = Environment.MachineName,
                compressed = method != CompressionMethod.None,
                compression = method.ToString(),
                excluded = exclusions.Where(e => e.IsExcluded).Select(e => e.Label).ToArray(),
                profiles = result.Profiles.Select(p => new
                {
                    name = p.Name,
                    sid = p.Sid,
                    sourcePath = p.SourcePath,
                    archive = p.ArchiveEntry,
                    files = p.Files,
                    bytes = p.Bytes,
                    excludedFolders = p.ExcludedFolders,
                    skippedCount = p.Skipped.Count,
                    skipped = p.Skipped.Take(MaxSkippedInManifest).Select(s => new { path = s.Path, reason = s.Reason }),
                }),
            };
            var entry = outer.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var s = entry.Open();
            JsonSerializer.Serialize(s, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        private static DateTimeOffset ClampTime(DateTime t)
        {
            var min = new DateTime(1980, 1, 1);
            var max = new DateTime(2107, 12, 31);
            return t < min ? min : t > max ? max : t;
        }

        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "profiili" : s;
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            string candidate = name;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; !used.Add(candidate); i++) candidate = $"{stem}_{i}{ext}";
            return candidate;
        }
    }

    /// <summary>Täsmää profiilikansioon suhteellisiin kansiopolkuihin. * = yksi taso, ** alussa = mikä tahansa syvyys.</summary>
    public sealed class ExclusionMatcher
    {
        private readonly List<(bool AnyDepth, string[] Segments)> _patterns = new();

        public ExclusionMatcher(IEnumerable<string> patterns)
        {
            foreach (var p in patterns)
            {
                var segs = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                bool any = segs.Length > 0 && segs[0] == "**";
                if (any) segs = segs.Skip(1).ToArray();
                if (segs.Length > 0) _patterns.Add((any, segs));
            }
        }

        public bool IsExcluded(string relativeDir)
        {
            var dir = relativeDir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (anyDepth, segs) in _patterns)
            {
                if (anyDepth)
                {
                    if (dir.Length >= segs.Length && Matches(dir, dir.Length - segs.Length, segs)) return true;
                }
                else if (dir.Length == segs.Length && Matches(dir, 0, segs)) return true;
            }
            return false;
        }

        private static bool Matches(string[] dir, int start, string[] segs)
        {
            for (int i = 0; i < segs.Length; i++)
                if (!FileSystemName.MatchesSimpleExpression(segs[i], dir[start + i], ignoreCase: true)) return false;
            return true;
        }
    }
}
