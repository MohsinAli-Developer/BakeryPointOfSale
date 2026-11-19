using System.ComponentModel.DataAnnotations;

namespace Application.Models
{
    public class Product
    {
        public int ProductId { get; set; }

        [Required(ErrorMessage = "Product name is required")]
        [StringLength(150)]
        public string ProductName { get; set; } = "";

        [Required(ErrorMessage = "Select a category")]
        [Range(1, int.MaxValue, ErrorMessage = "Select a category")]
        public int CategoryId { get; set; }

        [StringLength(50)]
        public string? Barcode { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Sale price must be ≥ 0")]
        public decimal SalePrice { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Purchase price must be ≥ 0")]
        public decimal PurchasePrice { get; set; }

        [Required(ErrorMessage = "Unit is required")]
        [StringLength(20)]
        public string Unit { get; set; } = "Pcs";

        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }
}
