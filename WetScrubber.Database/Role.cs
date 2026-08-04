using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WetScrubber.Database
{
    public class Role
    {
        public int RoleId { get; set; }

        [Required]
        [MaxLength(50)]
        public string RoleName { get; set; } = string.Empty;  // Admin, Engineer, Viewer

        // ── Navigation ───────────────────────────────────────────
        public ICollection<User> Users { get; set; } = new List<User>();
    }

}
