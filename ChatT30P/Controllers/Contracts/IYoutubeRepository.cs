using System;
using System.Collections.Generic;
using ChatT30P.Controllers.Models;

namespace ChatT30P.Contracts
{
    /// <summary>
    /// Youtube repository
    /// </summary>
    public interface IYoutubeRepository
    {
        /// <summary>
        /// Odnoklassniki users list
        /// </summary>
        /// <param name="commentType">Page type</param>
        /// <param name="take">Items to take</param>
        /// <param name="skip">Items to skip</param>
        /// <param name="filter">Filter expression</param>
        /// <param name="min">Memebers min</param>
        /// <param name="max">Memebers max</param>
        /// <param name="order">Sort order</param>
        /// <returns>List of users</returns>
        IEnumerable<YoutubeItem> GetYoutube(YoutubeType commentType = YoutubeType.All, int take = 10, int skip = 0,
            int min = 0, int max = int.MaxValue,
            string filter = "", string order = "");

    }
}

