using System.ComponentModel.DataAnnotations;

namespace MessangerWeb.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Email is required")]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }
    }
}
