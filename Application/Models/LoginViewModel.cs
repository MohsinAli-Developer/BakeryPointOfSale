using System.ComponentModel.DataAnnotations;

namespace Application.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Enter username")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Enter password")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }

        public string? ReturnUrl { get; set; }
    }
}
