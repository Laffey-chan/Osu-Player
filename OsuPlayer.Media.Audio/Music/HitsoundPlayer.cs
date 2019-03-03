﻿using Milky.OsuPlayer.Common;
using Milky.OsuPlayer.Common.Player;
using OSharp.Beatmap;
using OSharp.Beatmap.Sections.HitObject;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Milky.OsuPlayer.Common.Configuration;

namespace Milky.OsuPlayer.Media.Audio.Music
{
    internal class HitsoundPlayer : IPlayer, IDisposable
    {
        private static bool UseSoundTouch => Config.Current.Play.UsePlayerV2;
        public PlayerStatus PlayerStatus
        {
            get => _playerStatus;
            private set
            {
                Console.WriteLine(@"Hitsound: " + value);
                _playerStatus = value;
            }
        }

        public int Duration { get; private set; }

        public int PlayTime
        {
            get => (int)(_sw.ElapsedMilliseconds * _multiplier + _controlOffset);
            private set
            {
                _controlOffset = value;
                _sw.Reset();
            }
        }

        public int SingleOffset { get; set; }
        public bool IsPlaying => _playingTask != null && !_playingTask.IsCanceled && !_playingTask.IsCompleted &&
                                 !_playingTask.IsFaulted;

        public bool IsRunningDynamicOffset => _offsetTask != null && !_offsetTask.IsCanceled &&
                                              !_offsetTask.IsCompleted && !_offsetTask.IsFaulted;

        private readonly string _defaultDir = Domain.DefaultPath;
        private ConcurrentQueue<HitsoundElement> _hsQueue;
        private readonly List<HitsoundElement> _hitsoundList;
        private readonly string _filePath;

        // Play Control
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _controlOffset;
        private Task _playingTask, _offsetTask;
        private float _multiplier = 1f;
        private readonly Stopwatch _sw = new Stopwatch();
        private PlayerStatus _playerStatus;
        private PlayMod _mod;
        private readonly OsuFile _osuFile;

        public HitsoundPlayer(string filePath, OsuFile osuFile)
        {
            _osuFile = osuFile;
            _filePath = filePath;
            FileInfo fileInfo = new FileInfo(filePath);
            DirectoryInfo dirInfo = fileInfo.Directory;
            if (!fileInfo.Exists)
                throw new FileNotFoundException("文件不存在：" + filePath);
            if (dirInfo == null)
                throw new DirectoryNotFoundException("获取" + fileInfo.Name + "所在目录失败了？");

            List<HitsoundElement> hitsoundList = FillHitsoundList(osuFile, dirInfo);
            _hitsoundList = hitsoundList.OrderBy(t => t.Offset).ToList(); // Sorted before enqueue.
            Requeue();
            List<string> allPaths = hitsoundList.Select(t => t.FilePaths).SelectMany(sbx2 => sbx2).Distinct().ToList();
            foreach (var path in allPaths)
                WavePlayer.SaveToCache(path); // Cache each file once before play.

            PlayerStatus = PlayerStatus.Ready;
            SetPlayMod(Config.Current.Play.PlayMod, false);
        }

        public void SetPlayMod(PlayMod mod, bool play)
        {
            _mod = mod;
            switch (mod)
            {
                case PlayMod.None:
                    SetTime(PlayTime, play);
                    _multiplier = 1f;
                    break;
                case PlayMod.DoubleTime:
                case PlayMod.NightCore:
                    SetTime(PlayTime, play);
                    _multiplier = 1.5f;
                    break;
                case PlayMod.HalfTime:
                case PlayMod.DayCore:
                    SetTime(PlayTime, play);
                    _multiplier = 0.75f;
                    break;
            }
        }

        public void Play()
        {
            //if (IsPlaying)
            //    return;
            StartTask();
            //App.MusicPlayer.Play();
            if (_mod == PlayMod.None)
            {
                DynamicOffset();
            }
            _playingTask = new Task(PlayHitsound);
            _playingTask.Start();

            PlayerStatus = PlayerStatus.Playing;
        }

        public void Pause()
        {
            CancelTask();
            PlayerStatus = PlayerStatus.Paused;
            PlayTime = PlayTime;
        }

        public void Stop()
        {
            InnerStop(interrupt: true);
        }

        public void Replay()
        {
            Stop();
            Play();
        }

        public void SetTime(int ms, bool play = true)
        {
            Pause();
            int offsetMs = ms;
            PlayTime = offsetMs;
            Requeue(offsetMs);
        }

        public void Dispose()
        {
            Stop();
            if (_playingTask != null) Task.WaitAll(_playingTask);
            _playingTask?.Dispose();
            _cts?.Dispose();
            OnFinished = null;
            WavePlayer.ClearCache();
            GC.Collect();
        }

        internal void SetDuration(int musicPlayerDuration)
        {
            Duration = (int)Math.Ceiling(Math.Max(_hitsoundList.Max(k => k.Offset), musicPlayerDuration));
        }

        private void InnerStop(bool innerCall = false, bool interrupt = false)
        {
            CancelTask(innerCall);
            PlayTime = 0;
            PlayerStatus = interrupt ? PlayerStatus.Stopped : PlayerStatus.Finished;
            Requeue();
            if (!interrupt)
                OnFinished?.Invoke();
        }

        public event Action OnFinished;

        private void CancelTask(bool innerCall = false)
        {
            _cts.Cancel();
            if (!innerCall && _playingTask != null) Task.WaitAll(_playingTask);
            if (_offsetTask != null) Task.WaitAll(_offsetTask);
            Console.WriteLine(@"Task canceled.");
        }

        private void PlayHitsound()
        {
            _sw.Restart();
            while ((_hsQueue.Count > 0 && _mod == PlayMod.None) ||
                   ComponentPlayer.Current.MusicPlayer.PlayerStatus != PlayerStatus.Finished)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    _sw.Stop();
                    return;
                }

                // Loop
                if (_mod == PlayMod.None)
                {
                    while (_hsQueue.Count != 0 && _hsQueue.First().Offset <= PlayTime)
                    {
                        if (!_hsQueue.TryDequeue(out var hs)) continue;

                        foreach (var path in hs.FilePaths) Task.Run(() => WavePlayer.PlayFile(path, hs.Volume));
                    }
                }

                Thread.Sleep(1);
            }

            InnerStop(innerCall: true, interrupt: false);
        }

        private void DynamicOffset()
        {
            if (!IsRunningDynamicOffset)
            {
                _offsetTask = Task.Run(() =>
                {
                    Thread.Sleep(30);
                    int? preMs = null;
                    while (!_cts.IsCancellationRequested && ComponentPlayer.Current.MusicPlayer.PlayerStatus == PlayerStatus.Playing)
                    {
                        int playerMs = ComponentPlayer.Current.MusicPlayer.PlayTime;
                        if (playerMs != preMs)
                        {
                            preMs = playerMs;
                            var d = playerMs - (PlayTime + SingleOffset + (UseSoundTouch ? 480 : 0));
                            var r = Config.Current.Play.GeneralOffset - d;
                            if (Math.Abs(r) > 5)
                            {
                                //Console.WriteLine($@"music: {App.MusicPlayer.PlayTime}, hs: {PlayTime}, {d}({r})");
                                _controlOffset -= (int)(r / 2f);
                            }
                        }

                        Thread.Sleep(10);
                    }
                }, _cts.Token);
            }
        }

        private void Requeue(long skippedMs = 0)
        {
            _hsQueue = new ConcurrentQueue<HitsoundElement>();
            foreach (var i in _hitsoundList)
            {
                if (i.Offset < skippedMs)
                    continue;
                _hsQueue.Enqueue(i);
            }
        }

        private void StartTask()
        {
            _cts = new CancellationTokenSource();
        }

        #region Load

        private List<HitsoundElement> FillHitsoundList(OsuFile osuFile, DirectoryInfo dirInfo)
        {
            List<RawHitObject> hitObjects = _osuFile.HitObjects.HitObjectList;
            List<HitsoundElement> hitsoundList = new List<HitsoundElement>();

            var mapFiles = dirInfo.GetFiles("*.wav").Select(p => p.Name).ToArray();

            foreach (var obj in hitObjects)
            {
                if (obj.ObjectType == HitObjectType.Slider)
                {
                    foreach (var item in obj.SliderInfo.Edges)
                    {
                        //var currentTiming = file.TimingPoints.GetRedLine(item.Offset);
                        var currentLine = _osuFile.TimingPoints.GetLine(item.Offset);
                        var element = new HitsoundElement
                        {
                            GameMode = _osuFile.General.Mode,
                            Offset = item.Offset,
                            Volume = (obj.SampleVolume != 0 ? obj.SampleVolume : currentLine.Volume) / 100f,
                            Hitsound = item.EdgeHitsound,
                            Sample = item.EdgeSample,
                            Addition = item.EdgeAddition,
                            Track = currentLine.Track,
                            LineSample = currentLine.TimingSampleset,
                            CustomFile = obj.FileName,
                        };
                        SetFullPath(dirInfo, mapFiles, element);

                        hitsoundList.Add(element);
                    }
                }
                else
                {
                    //var currentTiming = file.TimingPoints.GetRedLine(obj.Offset);
                    var currentLine = _osuFile.TimingPoints.GetLine(obj.Offset);
                    var offset = obj.Offset; //todo: Spinner & hold

                    var element = new HitsoundElement
                    {
                        GameMode = _osuFile.General.Mode,
                        Offset = offset,
                        Volume = (obj.SampleVolume != 0 ? obj.SampleVolume : currentLine.Volume) / 100f,
                        Hitsound = obj.Hitsound,
                        Sample = obj.SampleSet,
                        Addition = obj.AdditionSet,
                        Track = currentLine.Track,
                        LineSample = currentLine.TimingSampleset,
                        CustomFile = obj.FileName,
                    };
                    SetFullPath(dirInfo, mapFiles, element);
                    hitsoundList.Add(element);
                }
            }

            return hitsoundList;
        }

        private void SetFullPath(DirectoryInfo dirInfo, string[] mapFiles, HitsoundElement element)
        {
            var files = element.FileNames;
            element.FilePaths = new string[files.Length];
            for (var i = 0; i < files.Length; i++)
            {
                var name = files[i];
                if (!mapFiles.Contains(name) || element.Track == 0 && string.IsNullOrEmpty(element.CustomFile))
                {
                    element.FilePaths[i] = Path.Combine(_defaultDir, element.DefaultFileNames[i]);
                }
                else
                {
                    element.FilePaths[i] = Path.Combine(dirInfo.FullName, name);
                }
            }
        }

        #endregion Load
    }
}