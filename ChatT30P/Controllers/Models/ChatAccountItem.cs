using System;

namespace ChatT30P.Controllers.Models
{
    public class ChatAccountItem
    {
        public string UserId { get; set; }
        public string Platform { get; set; }
        public string Phone { get; set; }
        public int Status { get; set; }
        public string ChatsJson { get; set; }
        public string AdsPowerId { get; set; }
        public DateTime? Created { get; set; }
    }
}
