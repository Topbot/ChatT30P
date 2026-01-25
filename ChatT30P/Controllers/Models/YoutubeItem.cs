﻿namespace ChatT30P.Controllers.Models
{
    /// <summary>
    /// Instagram user
    /// </summary>
    [System.Serializable]
    public class YoutubeItem : CustomItem
    {
        /// <summary>
        ///     Gets or sets UserName
        /// </summary>
        public string N { get; set; }

        /// <summary>
        ///     About
        /// </summary>
        public string A { get; set; }

        /// <summary>
        ///     Members/followers count
        /// </summary>
        public int? M { get; set; }

        /// <summary>
        ///     PostsCount
        /// </summary>
        public int? U { get; set; }

        /// <summary>
        ///     Videos per 3 months
        /// </summary>
        public int? U3 { get; set; }

        /// <summary>
        ///     Videos VIEWS per 3 months
        /// </summary>
        public int? U3V { get; set; }

        /// <summary>
        ///     Shorts per 3 months
        /// </summary>
        public int? S3 { get; set; }

        /// <summary>
        ///     Shorts VIEWS per 3 months
        /// </summary>
        public int? S3V { get; set; }

        /// <summary>
        ///     Количество MediaViews
        /// </summary>
        public long? V { get; set; }

        /// <summary>
        ///     Среднее число просмотров
        /// </summary>
        public long? VA { get; set; }

        /// <summary>
        ///     ISO code страны-языка
        /// </summary>
        public string c { get; set; }

        /// <summary>
        ///     Количество лайков за последние 5 роликов
        /// </summary>
        public int? L { get; set; }

        /// <summary>
        ///     Количество дизлайков за последние 5 роликов
        /// </summary>
        public int? D { get; set; }

        /// <summary>
        ///     Категории блогера
        /// </summary>
        public string T { get; set; }

        /// <summary>
        ///     Дата регистрации
        /// </summary>
        public string R { get; set; }
    }
}

