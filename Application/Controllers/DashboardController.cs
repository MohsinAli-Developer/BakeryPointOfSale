using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Application.Controllers
{
    [Authorize]
    public class DashboardController : Controller
    {
        public IActionResult Index() => View();
    }
}
