using Microsoft.AspNetCore.Mvc.Rendering;

namespace Application.Models
{
    public class ProductListViewModel
    {
        public List<Product> Products { get; set; } = new();
        public Dictionary<int, string> CategoryNames { get; set; } = new();
    }
    public class ProductFormViewModel
    {
        public Product Product { get; set; } = new();
        public List<SelectListItem> Categories { get; set; } = new();
    }
}
