using Microsoft.AspNetCore.Mvc;

namespace PersonalMediaManager.Web.Controllers;

[Route("/")]
public sealed class HomeController : Controller
{
    [AcceptVerbs("GET", "HEAD")]
    public IActionResult Index() => View("~/Views/Home/Index.cshtml");
}
