namespace Application.Models
{
    public class Sale
    {
        public int SaleId { get; set; }
        public string InvoiceNo { get; set; } = "";
        public DateTime SaleDate { get; set; } = DateTime.Now;
        public decimal SubTotal { get; set; }
        public decimal Discount { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal RemainingAmount { get; set; }
        public string PaymentMethod { get; set; } = "Cash";
        public int UserId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;

        public List<SaleDetail> Details { get; set; } = new();
    }

    public class SaleDetail
    {
        public int SaleDetailId { get; set; }
        public int SaleId { get; set; }
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Discount { get; set; }
        public decimal TotalAmount { get; set; }
    }

    public class InventoryTransaction
    {
        public int TransactionId { get; set; }
        public int ProductId { get; set; }
        public string TransactionType { get; set; } = "Sale";
        public int? SaleId { get; set; }
        public decimal QuantityIn { get; set; }
        public decimal QuantityOut { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.Now;
        public string? Remarks { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }

    public class PosViewModel
    {
        public string InvoiceNo { get; set; } = "";
        public List<PosProductDto> Products { get; set; } = new();
        public List<string> PaymentMethods { get; set; } = new()
    { "Cash", "Card", "EasyPaisa", "JazzCash", "Bank", "Other" };
    }

    public class PosProductDto
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public string? Barcode { get; set; }
        public decimal SalePrice { get; set; }
        public string Unit { get; set; } = "Pcs";
        public string CategoryName { get; set; } = "";
    }

    // POSTed from JS with the full cart
    public class CheckoutRequest
    {
        public decimal SubTotal { get; set; }
        public decimal Discount { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal PaidAmount { get; set; }
        public string PaymentMethod { get; set; } = "Cash";
        public List<CheckoutItem> Items { get; set; } = new();
    }

    public class CheckoutItem
    {
        public int ProductId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Discount { get; set; }
        public decimal TotalAmount { get; set; }
    }


}
