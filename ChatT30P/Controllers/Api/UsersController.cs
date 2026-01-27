using System;
using System.Collections.Generic;
using System.Net;
using System.Web;
using System.Web.Http;
using System.Web.Security;
using Core;
using ChatT30P.Core;

namespace ChatT30P.Controllers.Api
{
    public class UsersController : ApiController
    {
        private static bool IsAdmin()
        {
            return Security.IsAdmin;
        }

        public class UserDto
        {
            public string UserName { get; set; }
            public string Email { get; set; }
            public bool IsPaid { get; set; }
            public bool IsAdmin { get; set; }
        }

        [HttpGet]
        [Route("api/Users")]
        public IEnumerable<UserDto> Get()
        {
            if (!Security.IsAuthenticated)
                throw new HttpResponseException(HttpStatusCode.Unauthorized);

            if (!IsAdmin())
                throw new HttpResponseException(HttpStatusCode.Forbidden);

            int total;
            var users = Membership.GetAllUsers(0, int.MaxValue, out total);
            var list = new List<UserDto>();
            foreach (MembershipUser u in users)
            {
                var entity = MemberEntity.Load(u.UserName.ToLower());
                var isAdmin = entity != null && (string.Equals(entity.IsAdmin, "true", StringComparison.OrdinalIgnoreCase) || entity.IsAdmin == "1");
                var isPaid = entity != null && (string.Equals(entity.IsPaid, "true", StringComparison.OrdinalIgnoreCase) || entity.IsPaid == "1");
                list.Add(new UserDto
                {
                    UserName = u.UserName,
                    Email = u.Email,
                    IsPaid = isPaid,
                    IsAdmin = isAdmin
                });
            }
            return list;
        }

        [HttpPut]
        [Route("api/Users")]
        public IHttpActionResult Put(UserDto dto)
        {
            if (!Security.IsAuthenticated)
                return Unauthorized();

            if (!IsAdmin())
                return StatusCode(HttpStatusCode.Forbidden);

            if (dto == null || string.IsNullOrWhiteSpace(dto.UserName))
                return BadRequest();

            var user = Membership.GetUser(dto.UserName);
            if (user == null)
                return NotFound();

            // Persist flags in MemberEntity (this is what Security.IsPaid/IsAdmin uses)
            var entity = MemberEntity.Load(dto.UserName.ToLower()) ?? new MemberEntity(dto.UserName.ToLower());
            entity.IsPaid = dto.IsPaid ? "true" : "false";
            entity.IsAdmin = dto.IsAdmin ? "true" : "false";
            entity.Save(true);

            // Best-effort: keep membership flag in sync (may be ignored by provider)
            try
            {
                user.IsApproved = dto.IsPaid;
                Membership.UpdateUser(user);
            }
            catch
            {
            }

            return Ok();
        }

        [HttpDelete]
        [Route("api/Users")]
        public IHttpActionResult Delete(string username)
        {
            if (!Security.IsAuthenticated)
                return Unauthorized();

            if (!IsAdmin())
                return StatusCode(HttpStatusCode.Forbidden);

            if (string.IsNullOrWhiteSpace(username))
                return BadRequest();

            Membership.DeleteUser(username, true);

            try
            {
                var entity = MemberEntity.Load(username.ToLower());
                if (entity != null)
                {
                    entity.IsAdmin = "false";
                    entity.Save(true);
                }
            }
            catch
            {
            }

            return Ok();
        }
    }
}
