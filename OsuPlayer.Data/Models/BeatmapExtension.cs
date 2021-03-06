﻿using Milky.OsuPlayer.Shared;
using OSharp.Beatmap;
using osu.Shared;
using osu_database_reader.Components.Beatmaps;
using System.Collections.Generic;
using System.Linq;
using OSharpGameMode = OSharp.Beatmap.Sections.GamePlay.GameMode;

namespace Milky.OsuPlayer.Data.Models
{
    public static class BeatmapExtension
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentRandom Random = new ConcurrentRandom();

        public static Beatmap UpdateFromHolly(this Beatmap beatmap, BeatmapEntry entry)
        {
            beatmap.Artist = entry.Artist;
            beatmap.ArtistUnicode = entry.ArtistUnicode;
            beatmap.Title = entry.Title;
            beatmap.TitleUnicode = entry.TitleUnicode;
            beatmap.Creator = entry.Creator;
            beatmap.Version = entry.Version;
            beatmap.BeatmapFileName = entry.BeatmapFileName;
            beatmap.LastModifiedTime = entry.LastModifiedTime;
            beatmap.DiffSrNoneStandard = entry.DiffStarRatingStandard.ContainsKey(Mods.None)
                  ? entry.DiffStarRatingStandard[Mods.None]
                  : -1;
            beatmap.DiffSrNoneTaiko = entry.DiffStarRatingTaiko.ContainsKey(Mods.None)
                  ? entry.DiffStarRatingTaiko[Mods.None]
                  : -1;
            beatmap.DiffSrNoneCtB = entry.DiffStarRatingCtB.ContainsKey(Mods.None) ? entry.DiffStarRatingCtB[Mods.None] : -1;
            beatmap.DiffSrNoneMania = entry.DiffStarRatingMania.ContainsKey(Mods.None)
                  ? entry.DiffStarRatingMania[Mods.None]
                  : -1;
            beatmap.DrainTimeSeconds = entry.DrainTimeSeconds;
            beatmap.TotalTime = entry.TotalTime;
            beatmap.AudioPreviewTime = entry.AudioPreviewTime;
            beatmap.BeatmapId = entry.BeatmapId;
            beatmap.BeatmapSetId = entry.BeatmapSetId;
            beatmap.GameMode = entry.GameMode.ParseHollyToOSharp();
            beatmap.SongSource = entry.SongSource;
            beatmap.SongTags = entry.SongTags;
            beatmap.FolderName = entry.FolderName;
            beatmap.AudioFileName = entry.AudioFileName;

            return beatmap;
        }

        public static Beatmap ParseFromHolly(BeatmapEntry entry)
        {
            return (new Beatmap()).UpdateFromHolly(entry);
        }

        public static Beatmap UpdateFromOSharp(this Beatmap beatmap, OsuFile osuFile)
        {
            beatmap.Artist = osuFile.Metadata.Artist;
            beatmap.ArtistUnicode = osuFile.Metadata.ArtistUnicode;
            beatmap.Title = osuFile.Metadata.Title;
            beatmap.TitleUnicode = osuFile.Metadata.TitleUnicode;
            beatmap.Creator = osuFile.Metadata.Creator;
            beatmap.Version = osuFile.Metadata.Version;
            //BeatmapFileName = osuFile.BeatmapFileName;
            //LastModifiedTime = osuFile.LastModifiedTime;
            //DiffSrNoneStandard = osuFile.DiffStarRatingStandard.ContainsKey(Mods.None)
            //    ? osuFile.DiffStarRatingStandard[Mods.None]
            //    : -1;
            //DiffSrNoneTaiko = osuFile.DiffStarRatingTaiko.ContainsKey(Mods.None)
            //    ? osuFile.DiffStarRatingTaiko[Mods.None]
            //    : -1;
            //DiffSrNoneCtB = osuFile.DiffStarRatingCtB.ContainsKey(Mods.None) ? osuFile.DiffStarRatingCtB[Mods.None] : -1;
            //DiffSrNoneMania = osuFile.DiffStarRatingMania.ContainsKey(Mods.None)
            //    ? osuFile.DiffStarRatingMania[Mods.None]
            //    : -1;
            beatmap.DrainTimeSeconds = (int)(osuFile.HitObjects.MaxTime -
                                             osuFile.HitObjects.MinTime -
                                             osuFile.Events.Breaks.Select(k => k.EndTime - k.StartTime).Sum());
            beatmap.TotalTime = (int)osuFile.HitObjects.MaxTime;
            beatmap.AudioPreviewTime = osuFile.General.PreviewTime;
            beatmap.BeatmapId = osuFile.Metadata.BeatmapId;
            beatmap.BeatmapSetId = osuFile.Metadata.BeatmapSetId;
            beatmap.GameMode = osuFile.General.Mode;
            beatmap.SongSource = osuFile.Metadata.Source;
            beatmap.SongTags = string.Join(" ", osuFile.Metadata.TagList);
            //FolderName = osuFile.FolderName;
            beatmap.AudioFileName = osuFile.General.AudioFilename;

            return beatmap;
        }

        public static Beatmap ParseFromOSharp(OsuFile osuFile)
        {
            return (new Beatmap()).UpdateFromOSharp(osuFile);
        }

        public static OSharpGameMode ParseHollyToOSharp(this osu.Shared.GameMode gameMode)
        {
            return (OSharpGameMode)(int)gameMode;
        }

        public static Beatmap GetHighestDiff(this IEnumerable<Beatmap> enumerable)
        {
            var dictionary = enumerable.GroupBy(k => k.GameMode).ToDictionary(k => k.Key, k => k.ToList());
            if (dictionary.ContainsKey(OSharpGameMode.Circle))
            {
                return dictionary[OSharpGameMode.Circle]
                    .Aggregate((i1, i2) => i1.DiffSrNoneStandard > i2.DiffSrNoneStandard ? i1 : i2);
            }

            if (dictionary.ContainsKey(OSharpGameMode.Mania))
            {
                return dictionary[OSharpGameMode.Mania]
                    .Aggregate((i1, i2) => i1.DiffSrNoneMania > i2.DiffSrNoneMania ? i1 : i2);
            }

            if (dictionary.ContainsKey(OSharpGameMode.Catch))
            {
                return dictionary[OSharpGameMode.Catch]
                    .Aggregate((i1, i2) => i1.DiffSrNoneCtB > i2.DiffSrNoneCtB ? i1 : i2);
            }

            if (dictionary.ContainsKey(OSharpGameMode.Taiko))
            {
                return dictionary[OSharpGameMode.Taiko]
                    .Aggregate((i1, i2) => i1.DiffSrNoneTaiko > i2.DiffSrNoneTaiko ? i1 : i2);
            }

            Logger.Warn(@"Get highest difficulty failed.");
            var randKey = dictionary.Keys.ToList()[Random.Next(dictionary.Keys.Count)];
            return dictionary[randKey][dictionary[randKey].Count];
        }
    }
}