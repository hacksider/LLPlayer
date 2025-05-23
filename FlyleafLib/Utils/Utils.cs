﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CliWrap;
using Microsoft.Win32;

namespace FlyleafLib;

public static partial class Utils
{
    // VLC : https://github.com/videolan/vlc/blob/master/modules/gui/qt/dialogs/preferences/simple_preferences.cpp
    // Kodi: https://github.com/xbmc/xbmc/blob/master/xbmc/settings/AdvancedSettings.cpp

    public static List<string> ExtensionsAudio = new()
    {
        // VLC
          "3ga" , "669" , "a52" , "aac" , "ac3"
        , "adt" , "adts", "aif" , "aifc", "aiff"
        , "au"  , "amr" , "aob" , "ape" , "caf"
        , "cda" , "dts" , "flac", "it"  , "m4a"
        , "m4p" , "mid" , "mka" , "mlp" , "mod"
        , "mp1" , "mp2" , "mp3" , "mpc" , "mpga"
        , "oga" , "oma" , "opus", "qcp" , "ra"
        , "rmi" , "snd" , "s3m" , "spx" , "tta"
        , "voc" , "vqf" , "w64" , "wav" , "wma"
        , "wv"  , "xa"  , "xm"
    };

    public static List<string> ExtensionsPictures = new()
    {
        "apng", "bmp", "gif", "jpg", "jpeg", "png", "ico", "tif", "tiff", "tga","jfif"
    };

    public static List<string> ExtensionsSubtitlesText = new()
    {
        "ass", "ssa", "srt", "txt", "text", "vtt"
    };

    public static List<string> ExtensionsSubtitlesBitmap = new()
    {
        "sub", "sup"
    };

    public static List<string> ExtensionsSubtitles = [..ExtensionsSubtitlesText, ..ExtensionsSubtitlesBitmap];

    public static List<string> ExtensionsVideo = new()
    {
        // VLC
          "3g2" , "3gp" , "3gp2", "3gpp", "amrec"
        , "amv" , "asf" , "avi" , "bik" , "divx"
        , "drc" , "dv"  , "f4v" , "flv" , "gvi"
        , "gxf" , "m1v" , "m2t" , "m2v" , "m2ts"
        , "m4v" , "mkv" , "mov" , "mp2v", "mp4"
        , "mp4v", "mpa" , "mpe" , "mpeg", "mpeg1"
        , "mpeg2","mpeg4","mpg" , "mpv2", "mts"
        , "mtv" , "mxf" , "nsv" , "nuv" , "ogg"
        , "ogm" , "ogx" , "ogv" , "rec" , "rm"
        , "rmvb", "rpl" , "thp" , "tod" , "ts"
        , "tts" , "vob" , "vro" , "webm", "wmv"
        , "xesc"

        // Additional
        , "dav"
    };

    private static int uniqueId;
    public static int GetUniqueId() { Interlocked.Increment(ref uniqueId); return uniqueId; }

    /// <summary>
    /// Begin Invokes the UI thread to execute the specified action
    /// </summary>
    /// <param name="action"></param>
    public static void UI(Action action)
    {
#if DEBUG
        if (Application.Current == null)
            return;
#endif

        Application.Current.Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.DataBind);
    }

    /// <summary>
    /// Begin Invokes the UI thread if required to execute the specified action
    /// </summary>
    /// <param name="action"></param>
    public static void UIIfRequired(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == Application.Current.Dispatcher.Thread.ManagedThreadId)
            action();
        else
            Application.Current.Dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Invokes the UI thread to execute the specified action
    /// </summary>
    /// <param name="action"></param>
    public static void UIInvoke(Action action) => Application.Current.Dispatcher.Invoke(action);

    /// <summary>
    /// Invokes the UI thread if required to execute the specified action
    /// </summary>
    /// <param name="action"></param>
    public static void UIInvokeIfRequired(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == Application.Current.Dispatcher.Thread.ManagedThreadId)
            action();
        else
            Application.Current.Dispatcher.Invoke(action);
    }

    public static Thread STA(Action action)
    {
        Thread thread = new(() => action());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return thread;
    }

    public static void STAInvoke(Action action)
    {
        Thread thread = STA(action);
        thread.Join();
    }

    public static int Align(int num, int align)
    {
        int mod = num % align;
        return mod == 0 ? num : num + (align - mod);
    }
    public static float Scale(float value, float inMin, float inMax, float outMin, float outMax)
        => ((value - inMin) * (outMax - outMin) / (inMax - inMin)) + outMin;

    /// <summary>
    /// Adds a windows firewall rule if not already exists for the specified program path
    /// </summary>
    /// <param name="ruleName">Default value is Flyleaf</param>
    /// <param name="path">Default value is current executable path</param>
    public static void AddFirewallRule(string ruleName = null, string path = null)
    {
        Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(ruleName))
                    ruleName = "Flyleaf";

                if (string.IsNullOrEmpty(path))
                    path = Process.GetCurrentProcess().MainModule.FileName;

                path = $"\"{path}\"";

                // Check if rule already exists
                Process proc = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd",
                        Arguments = $"/C netsh advfirewall firewall show rule name={ruleName} verbose | findstr /L {path}",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput
                                        = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };

                proc.Start();
                proc.WaitForExit();

                if (proc.StandardOutput.Read() > 0)
                    return;

                // Add rule with admin rights
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd",
                        Arguments = $"/C netsh advfirewall firewall add rule name={ruleName} dir=in  action=allow enable=yes program={path} profile=any &" +
                                             $"netsh advfirewall firewall add rule name={ruleName} dir=out action=allow enable=yes program={path} profile=any",
                        Verb = "runas",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };

                proc.Start();
                proc.WaitForExit();

                Log($"Firewall rule \"{ruleName}\" added for {path}");
            }
            catch { }
        });
    }

    // We can't trust those
    //public static private bool    IsDesignMode=> (bool) DesignerProperties.IsInDesignModeProperty.GetMetadata(typeof(DependencyObject)).DefaultValue;
    //public static bool            IsDesignMode    = LicenseManager.UsageMode == LicenseUsageMode.Designtime; // Will not work properly (need to be called from non-static class constructor)

    //public static bool          IsWin11         = Regex.IsMatch(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "").ToString(), "Windows 11");
    //public static bool          IsWin10         = Regex.IsMatch(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "").ToString(), "Windows 10");
    //public static bool          IsWin8          = Regex.IsMatch(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "").ToString(), "Windows 8");
    //public static bool          IsWin7          = Regex.IsMatch(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "").ToString(), "Windows 7");

    public static List<string> GetMoviesSorted(List<string> movies)
    {
        List<string> moviesSorted = new();

        for (int i = 0; i < movies.Count; i++)
        {
            string ext = Path.GetExtension(movies[i]);

            if (ext == null || ext.Trim() == "")
                continue;

            if (ExtensionsVideo.Contains(ext[1..].ToLower()))
                moviesSorted.Add(movies[i]);
        }

        moviesSorted.Sort(new NaturalStringComparer());

        return moviesSorted;
    }
    public sealed class NaturalStringComparer : IComparer<string>
        { public int Compare(string a, string b) => NativeMethods.StrCmpLogicalW(a, b); }

    public static string GetRecInnerException(Exception e)
    {
        string dump = "";
        var cur = e.InnerException;

        for (int i = 0; i < 4; i++)
        {
            if (cur == null) break;
            dump += "\r\n - " + cur.Message;
            cur = cur.InnerException;
        }

        return dump;
    }
    public static string GetUrlExtention(string url)
        => url.LastIndexOf(".") > 0 ? url[(url.LastIndexOf(".") + 1)..].ToLower() : "";

    public static List<Language> GetSystemLanguages()
    {
        List<Language> Languages = [ Language.English ];

        if (CultureInfo.CurrentCulture.ThreeLetterISOLanguageName != "eng")
            Languages.Add(Language.Get(CultureInfo.CurrentCulture));

        foreach (System.Windows.Forms.InputLanguage lang in System.Windows.Forms.InputLanguage.InstalledInputLanguages)
            if (lang.Culture.ThreeLetterISOLanguageName != CultureInfo.CurrentCulture.ThreeLetterISOLanguageName && lang.Culture.ThreeLetterISOLanguageName != "eng")
                Languages.Add(Language.Get(lang.Culture));

        return Languages;
    }

    public class MediaParts
    {
        public int Season { get; set; }
        public int Episode { get; set; }
        public string Title { get; set; }
        public int Year { get; set; }
    }
    public static MediaParts GetMediaParts(string title, bool movieOnly = false)
    {
        Match res;
        MediaParts mp = new();
        List<int> indices = new();

        // s|season 01 ... e|episode|part 01
        res = Regex.Match(title, @"(^|[^a-z0-9])(s|season)[^a-z0-9]*(?<season>[0-9]{1,2})[^a-z0-9]*(e|episode|part)[^a-z0-9]*(?<episode>[0-9]{1,2})($|[^a-z0-9])", RegexOptions.IgnoreCase);
        if (!res.Success)
        {
            // 01x01
            res = Regex.Match(title, @"(^|[^a-z0-9])(?<season>[0-9]{1,2})x(?<episode>[0-9]{1,2})($|[^a-z0-9])", RegexOptions.IgnoreCase);

            // TODO: in case of single season should check only for e|episode|part 01
            if (!res.Success)
                res = Regex.Match(title, @"(^|[^a-z0-9])(episode|part)[^a-z0-9]*(?<episode>[0-9]{1,2})($|[^a-z0-9])", RegexOptions.IgnoreCase);
        }

        if (res.Groups.Count > 1)
        {
            if (res.Groups["season"].Value != "")
                mp.Season = int.Parse(res.Groups["season"].Value);

            if (res.Groups["episode"].Value != "")
                mp.Episode = int.Parse(res.Groups["episode"].Value);

            if (movieOnly)
                return mp;

            indices.Add(res.Index);
        }

        // non-movie words, 1080p, 2015
        indices.Add(Regex.Match(title, "[^a-z0-9]extended", RegexOptions.IgnoreCase).Index);
        indices.Add(Regex.Match(title, "[^a-z0-9]directors.cut", RegexOptions.IgnoreCase).Index);
        indices.Add(Regex.Match(title, "[^a-z0-9]brrip", RegexOptions.IgnoreCase).Index);
        indices.Add(Regex.Match(title, "[^a-z0-9][0-9]{3,4}p", RegexOptions.IgnoreCase).Index);

        res = Regex.Match(title, @"[^a-z0-9](?<year>(19|20)[0-9][0-9])($|[^a-z0-9])", RegexOptions.IgnoreCase);
        if (res.Success)
        {
            indices.Add(res.Index);
            mp.Year = int.Parse(res.Groups["year"].Value);
        }

        var sorted = indices.OrderBy(x => x);

        foreach (int index in sorted)
            if (index > 0)
            {
                title = title[..index];
                break;
            }

        title = title.Replace(".", " ").Replace("_", " ");
        title = Regex.Replace(title, @"\s{2,}", " ");
        title = Regex.Replace(title, @"[^a-z0-9]$", "", RegexOptions.IgnoreCase);

        mp.Title = title.Trim();

        return mp;
    }

    public static string FindNextAvailableFile(string fileName)
    {
        if (!File.Exists(fileName)) return fileName;

        string tmp = Path.Combine(Path.GetDirectoryName(fileName), Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"(.*) (\([0-9]+)\)$", "$1"));
        string newName;

        for (int i = 1; i < 101; i++)
        {
            newName = tmp + " (" + i + ")" + Path.GetExtension(fileName);
            if (!File.Exists(newName)) return newName;
        }

        return null;
    }
    public static string GetValidFileName(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    public static string FindFileBelow(string filename)
    {
        string current = AppDomain.CurrentDomain.BaseDirectory;

        while (current != null)
        {
            if (File.Exists(Path.Combine(current, filename)))
                return Path.Combine(current, filename);

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
    public static string GetFolderPath(string folder)
    {
        if (folder.StartsWith(":"))
        {
            folder = folder[1..];
            return FindFolderBelow(folder);
        }

        return Path.IsPathRooted(folder) ? folder : Path.GetFullPath(folder);
    }

    public static string FindFolderBelow(string folder)
    {
        string current = AppDomain.CurrentDomain.BaseDirectory;

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, folder)))
                return Path.Combine(current, folder);

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
    public static string GetUserDownloadPath() { try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders\").GetValue("{374DE290-123F-4565-9164-39C4925E467B}").ToString(); } catch (Exception) { return null; } }
    public static string DownloadToString(string url, int timeoutMs = 30000)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            return client.GetAsync(url).Result.Content.ReadAsStringAsync().Result;
        }
        catch (Exception e)
        {
            Log($"Download failed {e.Message} [Url: {url ?? "Null"}]");
        }

        return null;
    }

    public static MemoryStream DownloadFile(string url, int timeoutMs = 30000)
    {
        MemoryStream ms = new();

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            client.GetAsync(url).Result.Content.CopyToAsync(ms).Wait();
        }
        catch (Exception e)
        {
            Log($"Download failed {e.Message} [Url: {url ?? "Null"}]");
        }

        return ms;
    }

    public static bool DownloadFile(string url, string filename, int timeoutMs = 30000, bool overwrite = true)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using FileStream fs = new(filename, overwrite ? FileMode.Create : FileMode.CreateNew);
            client.GetAsync(url).Result.Content.CopyToAsync(fs).Wait();

            return true;
        }
        catch (Exception e)
        {
            Log($"Download failed {e.Message} [Url: {url ?? "Null"}, Path: {filename ?? "Null"}]");
        }

        return false;
    }
    public static string FixFileUrl(string url)
    {
        try
        {
            if (url == null || url.Length < 5)
                return url;

            if (url[..5].ToLower() == "file:")
                return new Uri(url).LocalPath;
        }
        catch { }

        return url;
    }

    /// <summary>
    /// Convert Windows lnk file path to target path
    /// </summary>
    /// <param name="filepath">lnk file path</param>
    /// <returns>targetPath or null</returns>
    public static string GetLnkTargetPath(string filepath)
    {
        try
        {
            // Using dynamic COM
            // ref: https://stackoverflow.com/a/49198242/9070784
            dynamic windowsShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            dynamic shortcut = windowsShell!.CreateShortcut(filepath);
            string targetPath = shortcut.TargetPath;

            if (string.IsNullOrEmpty(targetPath))
            {
                throw new InvalidOperationException("TargetPath is empty.");
            }

            return targetPath;
        }
        catch (Exception e)
        {
            Log($"Resolving Windows Link failed {e.Message} [FilePath: {filepath}]");

            return null;
        }
    }

    public static string GetBytesReadable(nuint i)
    {
        // Determine the suffix and readable value
        string suffix;
        double readable;
        if (i >= 0x1000000000000000) // Exabyte
        {
            suffix = "EB";
            readable = i >> 50;
        }
        else if (i >= 0x4000000000000) // Petabyte
        {
            suffix = "PB";
            readable = i >> 40;
        }
        else if (i >= 0x10000000000) // Terabyte
        {
            suffix = "TB";
            readable = i >> 30;
        }
        else if (i >= 0x40000000) // Gigabyte
        {
            suffix = "GB";
            readable = i >> 20;
        }
        else if (i >= 0x100000) // Megabyte
        {
            suffix = "MB";
            readable = i >> 10;
        }
        else if (i >= 0x400) // Kilobyte
        {
            suffix = "KB";
            readable = i;
        }
        else
        {
            return i.ToString("0 B"); // Byte
        }
        // Divide by 1024 to get fractional value
        readable /= 1024;
        // Return formatted number with suffix
        return readable.ToString("0.## ") + suffix;
    }
    static List<PerformanceCounter> gpuCounters;
    public static void GetGPUCounters()
    {
        PerformanceCounterCategory category = new("GPU Engine");
        string[] counterNames = category.GetInstanceNames();
        gpuCounters = new List<PerformanceCounter>();

        foreach (string counterName in counterNames)
            if (counterName.EndsWith("engtype_3D"))
                foreach (var counter in category.GetCounters(counterName))
                    if (counter.CounterName == "Utilization Percentage")
                        gpuCounters.Add(counter);
    }
    public static float GetGPUUsage()
    {
        float result = 0f;

        try
        {
            if (gpuCounters == null) GetGPUCounters();

            gpuCounters.ForEach(x => { _ = x.NextValue(); });
            Thread.Sleep(1000);
            gpuCounters.ForEach(x => { result += x.NextValue(); });

        }
        catch (Exception e) { Log($"[GPUUsage] Error {e.Message}"); result = -1f; GetGPUCounters(); }

        return result;
    }
    public static string GZipDecompress(string filename)
    {
        string newFileName = "";

        FileInfo fileToDecompress = new(filename);
        using (var originalFileStream = fileToDecompress.OpenRead())
        {
            string currentFileName = fileToDecompress.FullName;
            newFileName = currentFileName.Remove(currentFileName.Length - fileToDecompress.Extension.Length);

            using var decompressedFileStream = File.Create(newFileName);
            using GZipStream decompressionStream = new(originalFileStream, CompressionMode.Decompress);
            decompressionStream.CopyTo(decompressedFileStream);
        }

        return newFileName;
    }

    public static Dictionary<string, string> ParseQueryString(ReadOnlySpan<char> query)
    {
        Dictionary<string, string> dict = [];

        int nameStart   = 0;
        int equalPos    = -1;
        for (int i = 0; i < query.Length; i++)
        {
            if (query[i] == '=')
                equalPos = i;
            else if (query[i] == '&')
            {
                if (equalPos == -1)
                    dict[query[nameStart..i].ToString()] = null;
                else
                    dict[query[nameStart..equalPos].ToString()] = query.Slice(equalPos + 1, i - equalPos - 1).ToString();

                equalPos    = -1;
                nameStart   = i + 1;
            }
        }

        if (nameStart < query.Length - 1)
        {
            if (equalPos == -1)
                dict[query[nameStart..].ToString()] = null;
            else
                dict[query[nameStart..equalPos].ToString()] = query.Slice(equalPos + 1, query.Length - equalPos - 1).ToString();
        }

        return dict;
    }

    public unsafe static string BytePtrToStringUTF8(byte* bytePtr)
        => Marshal.PtrToStringUTF8((nint)bytePtr);

    public static System.Windows.Media.Color WinFormsToWPFColor(System.Drawing.Color sColor)
        => System.Windows.Media.Color.FromArgb(sColor.A, sColor.R, sColor.G, sColor.B);
    public static System.Drawing.Color WPFToWinFormsColor(System.Windows.Media.Color wColor)
        => System.Drawing.Color.FromArgb(wColor.A, wColor.R, wColor.G, wColor.B);

    public static System.Windows.Media.Color VorticeToWPFColor(Vortice.Mathematics.Color sColor)
        => System.Windows.Media.Color.FromArgb(sColor.A, sColor.R, sColor.G, sColor.B);
    public static Vortice.Mathematics.Color WPFToVorticeColor(System.Windows.Media.Color wColor)
        => new Vortice.Mathematics.Color(wColor.R, wColor.G, wColor.B, wColor.A);

    public static double SWFREQ_TO_TICKS =  10000000.0 / Stopwatch.Frequency;
    public static string ToHexadecimal(byte[] bytes)
    {
        StringBuilder hexBuilder = new();
        for (int i = 0; i < bytes.Length; i++)
        {
            hexBuilder.Append(bytes[i].ToString("x2"));
        }
        return hexBuilder.ToString();
    }
    public static int GCD(int a, int b) => b == 0 ? a : GCD(b, a % b);
    public static string TicksToTime(long ticks) => new TimeSpan(ticks).ToString();
    public static void Log(string msg) { try { Debug.WriteLine($"[{DateTime.Now:hh.mm.ss.fff}] {msg}"); } catch (Exception) { Debug.WriteLine($"[............] [MediaFramework] {msg}"); } }

    public static string TruncateString(string str, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(str))
            return str;

        if (str.Length <= maxLength)
            return str;

        int availableLength = maxLength - suffix.Length;

        if (availableLength <= 0)
        {
            return suffix.Substring(0, Math.Min(maxLength, suffix.Length));
        }

        return str.Substring(0, availableLength) + suffix;
    }

    // TODO: L: move to app, using event
    public static void PlayCompletionSound()
    {
        string soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets/completion.mp3");

        if (!File.Exists(soundPath))
        {
            return;
        }

        UI(() =>
        {
            try
            {
                // play completion sound
                System.Windows.Media.MediaPlayer mp = new();
                mp.Open(new Uri(soundPath));
                mp.Play();
            }
            catch
            {
                // ignored
            }
        });
    }

    public static string CommandToText(this Command cmd)
    {
        if (cmd.TargetFilePath.Any(char.IsWhiteSpace))
        {
            return $"& \"{cmd.TargetFilePath}\" {cmd.Arguments}";
        }

        return cmd.ToString();
    }
}
