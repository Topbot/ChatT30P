﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace ChatT30P.Controllers.Models
{
    /// <summary>
    /// Base item
    /// </summary>
    [Serializable]
    public class CustomItem
    {
        /// <summary>
        /// Id of club/user
        /// </summary>
        public string Id { get; set; }

        ///// <summary>
        /////     Price
        ///// </summary>
        //public int? Price { get; set; }

        ///// <summary>
        /////     Theme of public
        ///// </summary>
        //public string Theme { get; set; }

        ///// <summary>
        /////     Admin Contacts
        ///// </summary>
        //public string Contacts { get; set; }

        public override string ToString()
        {
            return Id;
        }
    }
}
