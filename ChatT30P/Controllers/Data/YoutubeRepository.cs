﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Core;
using ChatT30P.Contracts;
using ChatT30P.Controllers.Models;
using TopTools;
using System.Linq.Dynamic;
using System.Diagnostics;

namespace ChatT30P.Controllers.Data
{
    public class YoutubeRepository : IYoutubeRepository
    {
        /// <summary>
        /// Youtube list
        /// </summary>
        /// <param name="commentType">is private</param>
        /// <param name="take">Items to take</param>
        /// <param name="skip">Items to skip</param>
        /// <param name="filter">Filter expression</param>
        /// <param name="min">Memebers min</param>
        /// <param name="max">Memebers max</param>
        /// <param name="order">Sort order</param>
        /// <returns>List of comments</returns>
        public IEnumerable<YoutubeItem> GetYoutube(YoutubeType commentType = YoutubeType.All, int take = 10, int skip = 0,
            int min = 0, int max = int.MaxValue,
            string filter = "", string order = "")
        {
            using (var db = new t30pDataContext { CommandTimeout = 300 })
            {
                // Filter by followers range
                var query = db.top_youtubes.AsQueryable();
                if (max != 0)
                {
                    query = query.Where(i => i.followers >= min && i.followers <= max);
                }
                // Search by channel / name
                if (!string.IsNullOrEmpty(filter))
                {
                    query = query.Where(
                        i =>
                            i.id.StartsWith("youtube.com/" + filter) || i.name.Contains(filter)// || i.about.Contains(filter)
                            );
                }
                if (string.IsNullOrEmpty(order)) order = "followers desc";

                //switch (commentType)
                //{
                //    case YoutubeType.Male:
                //        query = query.Where(i => i.gender == "m");
                //        break;
                //    case YoutubeType.Female:
                //        query = query.Where(i => i.gender == "f");
                //        break;
                //    case YoutubeType.Other:
                //        query = query.Where(i => i.gender == "o");
                //        break;
                //    default:
                //        break;
                //}

                // if take passed in as 0, return all
                if (take == 0) take = 15000;
#if DEBUG
                take = 300;
#endif 
                return (from i in query.OrderBy(order) select i).Skip(skip).Take(take).ToList().Select(ToJson);
            }
        }

        /// <summary>
        /// Convert DB entity to DTO
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private YoutubeItem ToJson(top_youtube item)
        {
            return new YoutubeItem
            {
                Id = item.channelID,  //item.id,
                M = item.followers,
                N = item.name + (String.IsNullOrEmpty(item.email) ? "" : " 📧"),
                A = item.about,
                U = item.uploads,
                U3 = item.videos_3months??0,
                U3V = item.videos_3months_views??0,
                S3 = item.shorts_3months??0,
                S3V = item.shorts_3months_views??0,
                V = item.mediaviews,
                VA = item.averageviews,
                c = item.country ?? String.Empty,
                L = item.likes,
                D = item.dislikes,
                T = GetTopic(item.topic),
                R = item.createdat.HasValue?item.createdat.Value.ToString("yyyy-MM-dd"):String.Empty
            };
        }

        /// <summary>
        /// Youtube topics list
        /// </summary>
        private static List<string> Topics = new List<string>
        {
            "action_game",
            "association_football",
            "boxing",
            "entertainment",
            "fashion",
            "film",
            "food",
            "health",
            "hobby",
            "knowledge",
            "lifestyle_(sociology)",
            "mixed_martial_arts",
            "music",
            "pet",
            "physical_fitness",
            "politics",
            "rock_music",
            "role-playing_video_game",
            "society",
            "sport",
            "strategy_video_game",
            "technology",
            "tourism",
            "vehicle",
            "video_game_culture",
            "sports_game",
            "business",
            "humour",
            "performing_arts",
            "religion",
            "pop_music",
            "simulation_video_game",
            "action-adventure_game",
            "military",
            "basketball",
            "racing_video_game",
            "hip_hop_music",
            "electronic_music",
            "motorsport",
            "puzzle_video_game",
            "music_video_game",
            "television_program",
            "music_of_asia",
            "classical_music",
            "ice_hockey",
            "physical_attractiveness",
            "golf",
            "casual_game",
            "christian_music",
            "volleyball",
            "jazz",
            "tennis",
            "professional_wrestling",
            "reggae",
            "independent_music",
            "music_of_latin_america",
            "rhythm_and_blues",
            "soul_music"
        };

        /// <summary>
        /// Extract topic indices
        /// </summary>
        /// <param name="topic"></param>
        /// <returns></returns>
        private string GetTopic(string topic)
        {
            string sRet = String.Empty;
            if (!String.IsNullOrEmpty(topic))
            {
                for(int i = 0; i<Topics.Count; i++)
                {
                    if (topic.Contains(Topics[i]))
                    {
                        sRet += i + ",";
                    }
                }
#if DEBUG
                var newitem = topic.Split(',').FirstOrDefault(i => !Topics.Any(j => j == i));
                if (newitem!=null)
                {
                    Debug.WriteLine(topic + " = "+ newitem);
                }
#endif
            }
            return sRet.TrimEnd(',');
        }
    }
}
