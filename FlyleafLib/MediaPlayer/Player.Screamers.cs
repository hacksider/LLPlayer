﻿using System.Diagnostics;
using System.Threading;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;
using static FlyleafLib.Utils;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    /// <summary>
    /// Fires on Data frame when it's supposed to be shown according to the stream
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<DataFrame> OnDataFrame;

    /// <summary>
    /// Fires on buffering started
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler BufferingStarted;
    protected virtual void OnBufferingStarted()
    {
        if (onBufferingStarted != onBufferingCompleted) return;
        BufferingStarted?.Invoke(this, new EventArgs());
        onBufferingStarted++;

        if (CanDebug) Log.Debug($"OnBufferingStarted");
    }

    /// <summary>
    /// Fires on buffering completed (will fire also on failed buffering completed)
    /// (BufferDration > Config.Player.MinBufferDuration)
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<BufferingCompletedArgs> BufferingCompleted;
    protected virtual void OnBufferingCompleted(string error = null)
    {
        if (onBufferingStarted - 1 != onBufferingCompleted) return;

        if (error != null && LastError == null)
        {
            lastError = error;
            UI(() => LastError = LastError);
        }

        BufferingCompleted?.Invoke(this, new BufferingCompletedArgs(error));
        onBufferingCompleted++;
        if (CanDebug) Log.Debug($"OnBufferingCompleted{(error != null ? $" (Error: {error})" : "")}");
    }

    long    onBufferingStarted;
    long    onBufferingCompleted;

    int     vDistanceMs;
    int     aDistanceMs;
    int[]   sDistanceMss;
    int     dDistanceMs;
    int     sleepMs;

    long    elapsedTicks;
    long    elapsedSec;
    long    startTicks;
    long    showOneFrameTicks;

    int     allowedLateAudioDrops;
    long    lastSpeedChangeTicks;
    long    curLatency;
    internal long curAudioDeviceDelay;

    public int subNum => Config.Subtitles.Max;

    Stopwatch sw = new();

    private void ShowOneFrame()
    {
        for (int i = 0; i < subNum; i++)
        {
            sFrames[i] = null;
        }
        if (VideoDecoder.Frames.IsEmpty || !VideoDecoder.Frames.TryDequeue(out vFrame))
            return;

        renderer.Present(vFrame);

        if (!seeks.IsEmpty)
            return;

        if (!VideoDemuxer.IsHLSLive)
            curTime = vFrame.timestamp;

        UI(() => UpdateCurTime());

        for (int i = 0; i < subNum; i++)
        {
            // Prevents blinking after seek
            if (sFramesPrev[i] != null)
            {
                var cur = SubtitlesManager[i].GetCurrent();
                if (cur != null)
                {
                    if (!string.IsNullOrEmpty(cur.DisplayText) || (cur.IsBitmap && cur.Bitmap != null))
                    {
                        continue;
                    }
                }
            }

            // Clear last subtitles text if video timestamp is not within subs timestamp + duration (to prevent clearing current subs on pause/play)
            if (sFramesPrev[i] == null || sFramesPrev[i].timestamp > vFrame.timestamp || (sFramesPrev[i].timestamp + (sFramesPrev[i].duration * (long)10000)) < vFrame.timestamp)
            {
                sFramesPrev[i] = null;
                SubtitleClear(i);
            }
        }

        // Required for buffering on paused
        if (decoder.RequiresResync && !IsPlaying && seeks.IsEmpty)
            decoder.Resync(vFrame.timestamp);

        vFrame = null;
    }

    // !!! NEEDS RECODING (We show one frame, we dispose it, we get another one and we show it also after buffering which can be in 'no time' which can leave us without any more decoded frames so we rebuffer)
    private bool MediaBuffer()
    {
        if (CanTrace) Log.Trace("Buffering");

        while (isVideoSwitch && IsPlaying) Thread.Sleep(10);

        Audio.ClearBuffer();

        VideoDemuxer.Start();
        VideoDecoder.Start();

        if (Audio.isOpened && Config.Audio.Enabled)
        {
            curAudioDeviceDelay = Audio.GetDeviceDelay();

            if (AudioDecoder.OnVideoDemuxer)
                AudioDecoder.Start();
            else if (!decoder.RequiresResync)
            {
                AudioDemuxer.Start();
                AudioDecoder.Start();
            }
        }

        if (Config.Subtitles.Enabled)
        {
            for (int i = 0; i < subNum; i++)
            {
                if (!Subtitles[i].IsOpened)
                {
                    continue;
                }

                lock (lockSubtitles)
                {
                    if (SubtitlesDecoders[i].OnVideoDemuxer)
                    {
                        SubtitlesDecoders[i].Start();
                    }
                    //else if (!decoder.RequiresResync)
                    //{
                    //    SubtitlesDemuxers[i].Start();
                    //    SubtitlesDecoders[i].Start();
                    //}
                }
            }
        }

        if (Data.isOpened && Config.Data.Enabled)
        {
            if (DataDecoder.OnVideoDemuxer)
                DataDecoder.Start();
            else if (!decoder.RequiresResync)
            {
                DataDemuxer.Start();
                DataDecoder.Start();
            }
        }

        VideoDecoder.DisposeFrame(vFrame);
        vFrame = null;
        aFrame = null;
        for (int i = 0; i < subNum; i++)
        {
            sFrames[i] = null;
        }
        dFrame = null;

        bool gotAudio       = !Audio.IsOpened || Config.Player.MaxLatency != 0;
        bool gotVideo       = false;
        bool shouldStop     = false;
        bool showOneFrame   = true;
        int  audioRetries   = 4;
        int  loops          = 0;

        if (Config.Player.MaxLatency != 0)
        {
            lastSpeedChangeTicks = DateTime.UtcNow.Ticks;
            showOneFrame = false;
            Speed = 1;
        }

        do
        {
            loops++;

            if (showOneFrame && !VideoDecoder.Frames.IsEmpty)
            {
                ShowOneFrame();
                showOneFrameTicks = DateTime.UtcNow.Ticks;
                showOneFrame = false;
            }

            // We allo few ms to show a frame before cancelling
            if ((!showOneFrame || loops > 8) && !seeks.IsEmpty)
                return false;

            if (!gotVideo && !showOneFrame && !VideoDecoder.Frames.IsEmpty)
            {
                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (vFrame != null) gotVideo = true;
            }

            if (!gotAudio && aFrame == null && !AudioDecoder.Frames.IsEmpty)
                AudioDecoder.Frames.TryDequeue(out aFrame);

            if (gotVideo)
            {
                if (decoder.RequiresResync)
                    decoder.Resync(vFrame.timestamp);

                if (!gotAudio && aFrame != null)
                {
                    for (int i=0; i<Math.Min(20, AudioDecoder.Frames.Count); i++)
                    {
                        if (aFrame == null
                            || aFrame.timestamp - curAudioDeviceDelay > vFrame.timestamp
                            || vFrame.timestamp > Duration)
                        {
                            gotAudio = true;
                            break;
                        }

                        if (CanTrace) Log.Trace($"Drop aFrame {TicksToTime(aFrame.timestamp)}");
                        AudioDecoder.Frames.TryDequeue(out aFrame);
                    }

                    // Avoid infinite loop in case of all audio timestamps wrong
                    if (!gotAudio)
                    {
                        audioRetries--;

                        if (audioRetries < 1)
                        {
                            gotAudio = true;
                            aFrame = null;
                            Log.Warn($"Audio Exhausted 1");
                        }
                    }
                }
            }

            if (!IsPlaying || decoderHasEnded)
                shouldStop = true;
            else
            {
                if (!VideoDecoder.IsRunning && !isVideoSwitch)
                {
                    Log.Warn("Video Exhausted");
                    shouldStop= true;
                }

                if (gotVideo && !gotAudio && audioRetries > 0 && (!AudioDecoder.IsRunning || AudioDecoder.Demuxer.Status == MediaFramework.Status.QueueFull))
                {
                    if (CanWarn) Log.Warn($"Audio Exhausted 2 | {audioRetries}");

                    audioRetries--;

                    if (audioRetries < 1)
                        gotAudio  = true;
                }
            }

            Thread.Sleep(10);

        } while (!shouldStop && (!gotVideo || !gotAudio));

        if (shouldStop && !(decoderHasEnded && IsPlaying && vFrame != null))
        {
            Log.Info("Stopped");
            return false;
        }

        if (vFrame == null)
        {
            Log.Error("No Frames!");
            return false;
        }

        // Negative Buffer Duration during codec change (we don't dipose the cached frames or we receive them later) *skip waiting for now
        var bufDuration = GetBufferedDuration();
        if (bufDuration >= 0)
            while(seeks.IsEmpty && bufDuration < Config.Player.MinBufferDuration && IsPlaying && VideoDemuxer.IsRunning && VideoDemuxer.Status != MediaFramework.Status.QueueFull)
            {
                Thread.Sleep(20);
                bufDuration = GetBufferedDuration();
                if (bufDuration < 0)
                    break;
            }

        if (!seeks.IsEmpty)
            return false;

        if (CanInfo) Log.Info($"Started [V: {TicksToTime(vFrame.timestamp)}]" + (aFrame == null ? "" : $" [A: {TicksToTime(aFrame.timestamp)}]"));

        decoder.OpenedPlugin.OnBufferingCompleted();

        return true;
    }
    private void Screamer()
    {
        long audioBufferedDuration = 0; // We force audio resync with = 0

        while (Status == Status.Playing)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                requiresBuffering = true;

                for (int i = 0; i < subNum; i++)
                {
                    // Display subtitles from cache when seeking while playing
                    bool display = false;
                    var cur = SubtitlesManager[i].GetCurrent();
                    if (cur != null)
                    {
                        if (!string.IsNullOrEmpty(cur.DisplayText))
                        {
                            SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                            display = true;
                        }
                        else if (cur.IsBitmap && cur.Bitmap != null)
                        {
                            SubtitleDisplay(cur.Bitmap, i);
                            display = true;
                        }

                        if (display)
                        {
                            sFramesPrev[i] = new SubtitlesFrame
                            {
                                timestamp = cur.StartTime.Ticks + Config.Subtitles[i].Delay,
                                duration = (uint)cur.Duration.TotalMilliseconds,
                                isTranslated = cur.UseTranslated
                            };
                        }
                    }

                    // clear subtitles
                    // but do not clear when cache hit
                    if (!display && sFramesPrev[i] != null)
                    {
                        sFramesPrev[i] = null;
                        SubtitleClear(i);
                    }
                }

                decoder.PauseDecoders(); // TBR: Required to avoid gettings packets between Seek and ShowFrame which causes resync issues

                if (decoder.Seek(seekData.accurate ? Math.Max(0, seekData.ms - (int)new TimeSpan(Config.Player.SeekAccurateFixMargin).TotalMilliseconds) : seekData.ms, seekData.forward, !seekData.accurate) < 0) // Consider using GetVideoFrame with no timestamp (any) to ensure keyframe packet for faster seek in HEVC
                    Log.Warn("Seek failed");
                else if (seekData.accurate)
                    decoder.GetVideoFrame(seekData.ms * (long)10000);
            }

            if (requiresBuffering)
            {
                if (VideoDemuxer.Interrupter.Timedout)
                    break;

                OnBufferingStarted();
                MediaBuffer();
                requiresBuffering = false;
                if (!seeks.IsEmpty)
                    continue;

                if (vFrame == null)
                {
                    if (decoderHasEnded)
                        OnBufferingCompleted();

                    Log.Warn("[MediaBuffer] No video frame");
                    break;
                }

                // Temp fix to ensure we had enough time to decode one more frame
                int retries = 5;
                while (IsPlaying && VideoDecoder.Frames.Count == 0 && retries-- > 0)
                    Thread.Sleep(10);

                // Give enough time for the 1st frame to be presented
                while (IsPlaying && DateTime.UtcNow.Ticks - showOneFrameTicks < VideoDecoder.VideoStream.FrameDuration)
                    Thread.Sleep(4);

                OnBufferingCompleted();

                audioBufferedDuration = 0;
                allowedLateAudioDrops = 7;
                elapsedSec = 0;
                startTicks = vFrame.timestamp;
                sw.Restart();
            }

            if (Status != Status.Playing)
                break;

            if (vFrame == null)
            {
                if (VideoDecoder.Status == MediaFramework.Status.Ended)
                {
                    if (!MainDemuxer.IsHLSLive)
                    {
                        if (Math.Abs(MainDemuxer.Duration - curTime) < 2 * VideoDemuxer.VideoStream.FrameDuration)
                            curTime = MainDemuxer.Duration;
                        else
                            curTime += VideoDemuxer.VideoStream.FrameDuration;

                        UI(() => Set(ref _CurTime, curTime, true, nameof(CurTime)));
                    }

                    break;
                }

                Log.Warn("No video frames");
                requiresBuffering = true;
                continue;
            }

            if (aFrame == null && !isAudioSwitch)
                AudioDecoder.Frames.TryDequeue(out aFrame);

            for (int i = 0; i < subNum; i++)
            {
                if (sFrames[i] == null && !isSubsSwitches[i])
                    SubtitlesDecoders[i].Frames.TryPeek(out sFrames[i]);
            }

            if (dFrame == null && !isDataSwitch)
                DataDecoder.Frames.TryPeek(out dFrame);

            elapsedTicks = (long) (sw.ElapsedTicks * SWFREQ_TO_TICKS); // Do we really need ticks precision?

            vDistanceMs =
                  (int) ((((vFrame.timestamp - startTicks) / speed) - elapsedTicks) / 10000);

            if (aFrame != null)
            {
                curAudioDeviceDelay = Audio.GetDeviceDelay();
                audioBufferedDuration = Audio.GetBufferedDuration();
                aDistanceMs = (int) ((((aFrame.timestamp - startTicks) / speed) - (elapsedTicks - curAudioDeviceDelay)) / 10000);

                // Try to keep the audio buffer full enough to avoid audio crackling (up to 50ms)
                while (audioBufferedDuration > 0 && audioBufferedDuration < 50 * 10000 && aDistanceMs > -5 && aDistanceMs < 50)
                {
                    Audio.AddSamples(aFrame);

                    if (isAudioSwitch)
                    {
                        audioBufferedDuration = 0;
                        aDistanceMs = int.MaxValue;
                        aFrame = null;
                    }
                    else
                    {
                        audioBufferedDuration = Audio.GetBufferedDuration();
                        AudioDecoder.Frames.TryDequeue(out aFrame);
                        if (aFrame != null)
                            aDistanceMs = (int) ((((aFrame.timestamp - startTicks) / speed) - (elapsedTicks - curAudioDeviceDelay)) / 10000);
                        else
                            aDistanceMs = int.MaxValue;
                    }
                }
            }
            else
                aDistanceMs = int.MaxValue;

            for (int i = 0; i < subNum; i++)
            {
                sDistanceMss[i] = sFrames[i] != null
                    ? (int)((((sFrames[i].timestamp - startTicks) / speed) - elapsedTicks) / 10000)
                    : int.MaxValue;
            }

            dDistanceMs = dFrame != null
                ? (int)((((dFrame.timestamp - startTicks) / speed) - elapsedTicks) / 10000)
                : int.MaxValue;

            sleepMs = Math.Min(vDistanceMs, aDistanceMs) - 1;
            if (sleepMs < 0 || sleepMs == int.MaxValue)
                sleepMs = 0;

            if (sleepMs > 2)
            {
                if (vDistanceMs > 2000)
                {
                    Log.Warn($"vDistanceMs = {vDistanceMs} (restarting)");
                    requiresBuffering = true;
                    continue;
                }

                if (Engine.Config.UICurTimePerSecond &&  (
                    (!MainDemuxer.IsHLSLive && curTime / 10000000 != _CurTime / 10000000) ||
                    (MainDemuxer.IsHLSLive && Math.Abs(elapsedTicks - elapsedSec) > 10000000)))
                {
                    elapsedSec  = elapsedTicks;
                    UI(() => UpdateCurTime());
                }

                Thread.Sleep(sleepMs);
            }

            if (aFrame != null) // Should use different thread for better accurancy (renderer might delay it on high fps) | also on high offset we will have silence between samples
            {
                if (Math.Abs(aDistanceMs - sleepMs) <= 5)
                {
                    Audio.AddSamples(aFrame);

                    // Audio Desync - Large Buffer | ASampleBytes (S16 * 2 Channels = 4) * TimeBase * 2 -frames duration-
                    if (Audio.GetBufferedDuration() > Math.Max(50 * 10000, (aFrame.dataLen / 4) * Audio.Timebase * 2))
                    {
                        if (CanDebug)
                            Log.Debug($"Audio desynced by {(int)(audioBufferedDuration / 10000)}ms, clearing buffers");

                        Audio.ClearBuffer();
                        audioBufferedDuration = 0;
                    }

                    aFrame = null;
                }
                else if (aDistanceMs > 4000) // Drops few audio frames in case of wrong timestamps (Note this should be lower, until swr has min/max samples for about 20-70ms)
                {
                    if (allowedLateAudioDrops > 0)
                    {
                        Audio.framesDropped++;
                        allowedLateAudioDrops--;
                        if (CanDebug) Log.Debug($"aDistanceMs 3 = {aDistanceMs}");
                        aFrame = null;
                        audioBufferedDuration = 0;
                    }
                }
                else if (aDistanceMs < -5) // Will be transfered back to decoder to drop invalid timestamps
                {
                    if (CanTrace) Log.Trace($"aDistanceMs = {aDistanceMs} | AudioFrames: {AudioDecoder.Frames.Count} AudioPackets: {AudioDecoder.Demuxer.AudioPackets.Count}");

                    if (GetBufferedDuration() < Config.Player.MinBufferDuration / 2)
                    {
                        if (CanInfo)
                            Log.Warn($"Not enough buffer (restarting)");

                        requiresBuffering = true;
                        continue;
                    }

                    audioBufferedDuration = 0;

                    if (aDistanceMs < -600)
                    {
                        if (CanTrace) Log.Trace($"All audio frames disposed");
                        Audio.framesDropped += AudioDecoder.Frames.Count;
                        AudioDecoder.DisposeFrames();
                        aFrame = null;
                    }
                    else
                    {
                        int maxdrop = Math.Max(Math.Min(vDistanceMs - sleepMs - 1, 20), 3);
                        for (int i=0; i<maxdrop; i++)
                        {
                            if (CanTrace) Log.Trace($"aDistanceMs 2 = {aDistanceMs}");
                            Audio.framesDropped++;
                            AudioDecoder.Frames.TryDequeue(out aFrame);

                            if (aFrame == null || ((aFrame.timestamp - startTicks) / speed) - ((long) (sw.ElapsedTicks * SWFREQ_TO_TICKS) - Audio.GetDeviceDelay() + 8 * 1000) > 0)
                                break;

                            aFrame = null;
                        }
                    }
                }
            }

            if (Math.Abs(vDistanceMs - sleepMs) <= 2)
            {
                if (CanTrace) Log.Trace($"[V] Presenting {TicksToTime(vFrame.timestamp)}");

                if (decoder.VideoDecoder.Renderer.Present(vFrame, false))
                    Video.framesDisplayed++;
                else
                    Video.framesDropped++;

                lock (seeks)
                    if (seeks.IsEmpty)
                    {
                        curTime = !MainDemuxer.IsHLSLive ? vFrame.timestamp : VideoDemuxer.CurTime;

                        if (Config.Player.UICurTimePerFrame)
                            UI(() => UpdateCurTime());
                    }

                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (vFrame != null && Config.Player.MaxLatency != 0)
                    CheckLatency();
            }
            else if (vDistanceMs < -2)
            {
                if (vDistanceMs < -10 || GetBufferedDuration() < Config.Player.MinBufferDuration / 2)
                {
                    if (CanDebug)
                        Log.Debug($"vDistanceMs = {vDistanceMs} (restarting)");

                    requiresBuffering = true;
                    continue;
                }

                if (CanDebug)
                    Log.Debug($"vDistanceMs = {vDistanceMs}");

                Video.framesDropped++;
                VideoDecoder.DisposeFrame(vFrame);
                VideoDecoder.Frames.TryDequeue(out vFrame);
            }

            // Set the current time to SubtitleManager in a loop
            if (Config.Subtitles.Enabled)
            {
                for (int i = 0; i < subNum; i++)
                {
                    if (Subtitles[i].Enabled)
                    {
                        SubtitlesManager[i].SetCurrentTime(new TimeSpan(curTime));
                    }
                }
            }
            // Internal subtitles (Text or Bitmap or OCR)
            for (int i = 0; i < subNum; i++)
            {
                if (sFramesPrev[i] != null && ((sFramesPrev[i].timestamp - startTicks + (sFramesPrev[i].duration * (long)10000)) / speed) - (long) (sw.ElapsedTicks * SWFREQ_TO_TICKS) < 0)
                {
                    SubtitleClear(i);

                    sFramesPrev[i] = null;
                }

                if (sFrames[i] != null)
                {
                    if (Math.Abs(sDistanceMss[i] - sleepMs) < 30 || (sDistanceMss[i] < -30 && sFrames[i].duration + sDistanceMss[i] > 0))
                    {
                        if (sFrames[i].isBitmap && sFrames[i].sub.num_rects > 0)
                        {
                            if (SubtitlesSelectedHelper.GetMethod(i) == SelectSubMethod.OCR)
                            {
                                // Prevent the problem of OCR subtitles not being used in priority by setting
                                // the timestamp of the subtitles as they are displayed a little earlier
                                SubtitlesManager[i].SetCurrentTime(new TimeSpan(sFrames[i].timestamp));
                            }

                            var cur = SubtitlesManager[i].GetCurrent();

                            if (cur != null && !string.IsNullOrEmpty(cur.Text))
                            {
                                // Use OCR text subtitles if available
                                SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                            }
                            else
                            {
                                // renderer.CreateOverlayTexture(sFrame, SubtitlesDecoder.CodecCtx->width, SubtitlesDecoder.CodecCtx->height);
                                SubtitleDisplay(sFrames[i].bitmap, i);
                            }

                            SubtitlesDecoder.DisposeFrame(sFrames[i]);  // only rects
                        }
                        else if (sFrames[i].isBitmap && sFrames[i].sub.num_rects == 0)
                        {
                            // For Blu-ray subtitles (PGS), clear the previous subtitle
                            SubtitleClear(i);
                            sFramesPrev[i] = sFrames[i] = null;
                        }
                        else
                        {
                            // internal text sub (does not support translate)
                            SubtitleDisplay(sFrames[i].text, i, false);
                        }
                        sFramesPrev[i] = sFrames[i];
                        sFrames[i] = null;
                        SubtitlesDecoders[i].Frames.TryDequeue(out _);
                    }
                    else if (sDistanceMss[i] < -30)
                    {
                        if (CanDebug)
                            Log.Debug($"sDistanceMss[i] = {sDistanceMss[i]}");

                        //SubtitleClear(i);

                        // TODO: L: Here sFrames can be null, occurs when switching subtitles?
                        SubtitlesDecoder.DisposeFrame(sFrames[i]);
                        sFrames[i] = null;
                        SubtitlesDecoders[i].Frames.TryDequeue(out _);
                    }
                }
            }

            // External or ASR subtitles
            for (int i = 0; i < subNum; i++)
            {
                if (!Config.Subtitles.Enabled || !Subtitles[i].Enabled)
                {
                    continue;
                }

                SubtitleData cur = SubtitlesManager[i].GetCurrent();

                if (cur == null)
                {
                    continue;
                }

                if (sFramesPrev[i] == null ||
                    sFramesPrev[i].timestamp != cur.StartTime.Ticks + Config.Subtitles[i].Delay)
                {
                    bool display = false;
                    if (!string.IsNullOrEmpty(cur.DisplayText))
                    {
                        SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                        display = true;
                    }
                    else if (cur.IsBitmap && cur.Bitmap != null)
                    {
                        SubtitleDisplay(cur.Bitmap, i);
                        display = true;
                    }

                    if (display)
                    {
                        sFramesPrev[i] = new SubtitlesFrame
                        {
                            timestamp = cur.StartTime.Ticks + Config.Subtitles[i].Delay,
                            duration = (uint)cur.Duration.TotalMilliseconds,
                            isTranslated = cur.UseTranslated
                        };
                    }
                }
                else
                {
                    // Apply translation to current sub
                    if (Config.Subtitles[i].EnabledTranslated) {

                        // If the subtitle currently playing is not translated, change to the translated for display
                        if (sFramesPrev[i] != null &&
                            sFramesPrev[i].timestamp == cur.StartTime.Ticks + Config.Subtitles[i].Delay &&
                            sFramesPrev[i].isTranslated != cur.UseTranslated)
                        {
                            SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                            sFramesPrev[i].isTranslated = cur.UseTranslated;
                        }
                    }
                }
            }

            if (dFrame != null)
            {
                if (Math.Abs(dDistanceMs - sleepMs) < 30 || (dDistanceMs < -30))
                {
                    OnDataFrame?.Invoke(this, dFrame);

                    dFrame = null;
                    DataDecoder.Frames.TryDequeue(out var devnull);
                }
                else if (dDistanceMs < -30)
                {
                    if (CanDebug)
                        Log.Debug($"dDistanceMs = {dDistanceMs}");

                    dFrame = null;
                    DataDecoder.Frames.TryDequeue(out var devnull);
                }
            }
        }

        if (CanInfo) Log.Info($"Finished -> {TicksToTime(CurTime)}");
    }

    private void CheckLatency()
    {
        curLatency = GetBufferedDuration();

        if (CanDebug)
            Log.Debug($"[Latency {curLatency/10000}ms] Frames: {VideoDecoder.Frames.Count} Packets: {VideoDemuxer.VideoPackets.Count} Speed: {speed}");

        if (curLatency <= Config.Player.MinLatency) // We've reached the down limit (back to speed x1)
        {
            ChangeSpeedWithoutBuffering(1);
            return;
        }
        else if (curLatency < Config.Player.MaxLatency)
            return;

        var newSpeed = Math.Max(Math.Round((double)curLatency / Config.Player.MaxLatency, 1, MidpointRounding.ToPositiveInfinity), 1.1);

        if (newSpeed > 4) // TBR: dispose only as much as required to avoid rebuffering
        {
            decoder.Flush();
            requiresBuffering = true;
            Log.Debug($"[Latency {curLatency/10000}ms] Clearing queue");
            return;
        }

        ChangeSpeedWithoutBuffering(newSpeed);
    }
    private void ChangeSpeedWithoutBuffering(double newSpeed)
    {
        if (speed == newSpeed)
            return;

        long curTicks = DateTime.UtcNow.Ticks;

        if (newSpeed != 1 && curTicks - lastSpeedChangeTicks < Config.Player.LatencySpeedChangeInterval)
            return;

        lastSpeedChangeTicks = curTicks;

        if (CanDebug)
            Log.Debug($"[Latency {curLatency/10000}ms] Speed changed x{speed} -> x{newSpeed}");

        if (aFrame != null)
            AudioDecoder.FixSample(aFrame, speed, newSpeed);

        Speed       = newSpeed;
        requiresBuffering
                    = false;
        startTicks  = curTime;
        elapsedSec  = 0;
        sw.Restart();
    }
    private long GetBufferedDuration()
    {
        var decoder = VideoDecoder.Frames.IsEmpty ? 0 : VideoDecoder.Frames.ToArray()[^1].timestamp - vFrame.timestamp;
        var demuxer = VideoDemuxer.VideoPackets.IsEmpty || VideoDemuxer.VideoPackets.LastTimestamp == NoTs
            ? 0 :
            (VideoDemuxer.VideoPackets.LastTimestamp - VideoDemuxer.StartTime) - vFrame.timestamp;

        return Math.Max(decoder, demuxer);
    }

    private void AudioBuffer()
    {
        if (CanTrace) Log.Trace("Buffering");

        while ((isVideoSwitch || isAudioSwitch) && IsPlaying)
            Thread.Sleep(10);

        if (!IsPlaying)
            return;

        aFrame = null;
        Audio.ClearBuffer();
        decoder.AudioStream.Demuxer.Start();
        AudioDecoder.Start();

        while(AudioDecoder.Frames.IsEmpty && IsPlaying && AudioDecoder.IsRunning)
            Thread.Sleep(10);

        AudioDecoder.Frames.TryPeek(out aFrame);

        if (aFrame == null)
            return;

        lock (seeks)
            if (seeks.IsEmpty)
            {
                curTime = !MainDemuxer.IsHLSLive ? aFrame.timestamp : MainDemuxer.CurTime;
                UI(() =>
                {
                    Set(ref _CurTime, curTime, true, nameof(CurTime));
                    UpdateBufferedDuration();
                });
            }

        while(seeks.IsEmpty && decoder.AudioStream.Demuxer.BufferedDuration < Config.Player.MinBufferDuration && AudioDecoder.Frames.Count < Config.Decoder.MaxAudioFrames / 2 && IsPlaying && decoder.AudioStream.Demuxer.IsRunning && decoder.AudioStream.Demuxer.Status != MediaFramework.Status.QueueFull)
            Thread.Sleep(20);
    }
    private void ScreamerAudioOnly()
    {
        long bufferedDuration = 0;

        while (IsPlaying)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                requiresBuffering = true;

                if (AudioDecoder.OnVideoDemuxer)
                {
                    if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 1");
                }
                else
                {
                    if (decoder.SeekAudio(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 2");
                }
            }

            if (requiresBuffering)
            {
                OnBufferingStarted();
                AudioBuffer();
                requiresBuffering = false;

                if (!seeks.IsEmpty)
                    continue;

                if (!IsPlaying || AudioDecoder.Frames.IsEmpty)
                    break;

                OnBufferingCompleted();
            }

            if (AudioDecoder.Frames.IsEmpty)
            {
                if (bufferedDuration == 0)
                {
                    if (!IsPlaying || AudioDecoder.Status == MediaFramework.Status.Ended)
                        break;

                    Log.Warn("No audio frames");
                    requiresBuffering = true;
                }
                else
                {
                    Thread.Sleep(50); // waiting for audio buffer to be played before end
                    bufferedDuration = Audio.GetBufferedDuration();
                }

                continue;
            }

            // Support only ASR subtitle for audio
            foreach (int i in SubtitlesASR.SubIndexSet)
            {
                SubtitlesManager[i].SetCurrentTime(new TimeSpan(curTime));

                var cur = SubtitlesManager[i].GetCurrent();
                if (cur != null && !string.IsNullOrEmpty(cur.DisplayText))
                {
                    SubtitleDisplay(cur.DisplayText, i, cur.UseTranslated);
                }
                else
                {
                    SubtitleClear(i);
                }
            }

            bufferedDuration = Audio.GetBufferedDuration();

            if (bufferedDuration < 300 * 10000)
            {
                do
                {
                    AudioDecoder.Frames.TryDequeue(out aFrame);
                    if (aFrame == null || !IsPlaying)
                        break;

                    Audio.AddSamples(aFrame);
                    bufferedDuration += (long) ((aFrame.dataLen / 4) * Audio.Timebase);
                    curTime = !MainDemuxer.IsHLSLive ? aFrame.timestamp : MainDemuxer.CurTime;
                } while (bufferedDuration < 100 * 10000);

                lock (seeks)
                    if (seeks.IsEmpty)
                    {
                        if (!Engine.Config.UICurTimePerSecond || curTime / 10000000 != _CurTime / 10000000)
                        {
                            UI(() =>
                            {
                                Set(ref _CurTime, curTime, true, nameof(CurTime));
                                UpdateBufferedDuration();
                            });
                        }
                    }

                Thread.Sleep(20);
            }
            else
                Thread.Sleep(50);
        }
    }

    private void ScreamerReverse()
    {
        while (Status == Status.Playing)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                    Log.Warn("Seek failed");
            }

            if (vFrame == null)
            {
                if (VideoDecoder.Status == MediaFramework.Status.Ended)
                    break;

                OnBufferingStarted();
                if (reversePlaybackResync)
                {
                    decoder.Flush();
                    VideoDemuxer.EnableReversePlayback(CurTime);
                    reversePlaybackResync = false;
                }
                VideoDemuxer.Start();
                VideoDecoder.Start();

                while (VideoDecoder.Frames.IsEmpty && Status == Status.Playing && VideoDecoder.IsRunning) Thread.Sleep(15);
                OnBufferingCompleted();
                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (vFrame == null) { Log.Warn("No video frame"); break; }
                vFrame.timestamp = (long) (vFrame.timestamp / Speed);

                startTicks = vFrame.timestamp;
                sw.Restart();
                elapsedSec = 0;

                if (!MainDemuxer.IsHLSLive && seeks.IsEmpty)
                    curTime = (long) (vFrame.timestamp * Speed);
                UI(() => UpdateCurTime());
            }

            elapsedTicks    = startTicks - (long) (sw.ElapsedTicks * SWFREQ_TO_TICKS);
            vDistanceMs     = (int) ((elapsedTicks - vFrame.timestamp) / 10000);
            sleepMs         = vDistanceMs - 1;

            if (sleepMs < 0) sleepMs = 0;

            if (Math.Abs(vDistanceMs - sleepMs) > 5)
            {
                //Log($"vDistanceMs |-> {vDistanceMs}");
                VideoDecoder.DisposeFrame(vFrame);
                vFrame = null;
                Thread.Sleep(5);
                continue; // rebuffer
            }

            if (sleepMs > 2)
            {
                if (sleepMs > 1000)
                {
                    //Log($"sleepMs -> {sleepMs} , vDistanceMs |-> {vDistanceMs}");
                    VideoDecoder.DisposeFrame(vFrame);
                    vFrame = null;
                    Thread.Sleep(5);
                    continue; // rebuffer
                }

                // Every seconds informs the application with CurTime / Bitrates (invokes UI thread to ensure the updates will actually happen)
                if (Engine.Config.UICurTimePerSecond && (
                    (!MainDemuxer.IsHLSLive && curTime / 10000000 != _CurTime / 10000000) ||
                    (MainDemuxer.IsHLSLive && Math.Abs(elapsedTicks - elapsedSec) > 10000000)))
                {
                    elapsedSec  = elapsedTicks;
                    UI(() => UpdateCurTime());
                }

                Thread.Sleep(sleepMs);
            }

            decoder.VideoDecoder.Renderer.Present(vFrame, false);
            if (!MainDemuxer.IsHLSLive && seeks.IsEmpty)
            {
                curTime = (long) (vFrame.timestamp * Speed);

                if (Config.Player.UICurTimePerFrame)
                    UI(() => UpdateCurTime());
            }

            VideoDecoder.Frames.TryDequeue(out vFrame);
            if (vFrame != null)
                vFrame.timestamp = (long) (vFrame.timestamp / Speed);
        }
    }
}

public class BufferingCompletedArgs : EventArgs
{
    public string   Error       { get; }
    public bool     Success     { get; }

    public BufferingCompletedArgs(string error)
    {
        Error   = error;
        Success = Error == null;
    }
}
