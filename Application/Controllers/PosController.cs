using Application.Data;
using Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;

namespace Application.Controllers
{

    [Authorize]
    public class PosController : Controller
    {
        private readonly string _connStr;

        public PosController(IConfiguration config)
        {
            _connStr = config.GetConnectionString("DefaultConnection")!;
        }

        // ─────────────────────────────────────────────────────────
        // GET: /Pos  →  load active products + next invoice no
        // ─────────────────────────────────────────────────────────
        public async Task<IActionResult> Index()
        {
            var products = new List<PosProductDto>();

            await using (var conn = new SqlConnection(_connStr))
            {
                await conn.OpenAsync();

                const string sql = @"
                SELECT p.ProductId, p.ProductName, p.Barcode,
                       p.SalePrice, p.Unit, c.CategoryName
                FROM Products p
                INNER JOIN Categories c ON c.CategoryId = p.CategoryId
                WHERE p.IsActive = 1
                ORDER BY p.ProductName;";

                await using var cmd = new SqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync();

                while (await rd.ReadAsync())
                {
                    products.Add(new PosProductDto
                    {
                        ProductId = rd.GetInt32(0),
                        ProductName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        Barcode = rd.IsDBNull(2) ? null : rd.GetString(2),
                        SalePrice = rd.GetDecimal(3),
                        Unit = rd.IsDBNull(4) ? "Pcs" : rd.GetString(4),
                        CategoryName = rd.IsDBNull(5) ? "" : rd.GetString(5)
                    });
                }
            }

            var vm = new PosViewModel
            {
                InvoiceNo = await GenerateInvoiceNoAsync(),
                Products = products
            };

            return View(vm);
        }

        // ─────────────────────────────────────────────────────────
        // POST: /Pos/Checkout  →  save sale + details + inventory
        // ─────────────────────────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req)
        {
            // ── 1. Basic validation ──────────────────────────────
            if (req == null || req.Items == null || req.Items.Count == 0)
                return BadRequest(new { message = "Cart is empty." });

            if (req.PaidAmount < 0 || req.Discount < 0)
                return BadRequest(new { message = "Invalid amounts." });

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdClaim))
                return Unauthorized(new { message = "User not authenticated." });

            var userId = int.Parse(userIdClaim);

            var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();

            // ── 2. Confirm user still active ─────────────────────
            {
                await using var cmd = new SqlCommand(
                    "SELECT COUNT(1) FROM Users WHERE UserId = @uid AND IsActive = 1", conn);
                cmd.Parameters.Add("@uid", SqlDbType.Int).Value = userId;

                var exists = (int)(await cmd.ExecuteScalarAsync() ?? 0);
                if (exists == 0)
                    return Unauthorized(new { message = "Your session is no longer valid. Please log in again." });
            }

            // ── 3. Load products from DB (one query, plain IN) ───
            var dbProducts = new Dictionary<int, Product>();

            {
                // Build @p0, @p1, @p2 ... parameters — SAFE (no string concatenation of values)
                var paramNames = productIds.Select((_, i) => "@p" + i).ToArray();
                var inClause = string.Join(",", paramNames);

                var sql = $@"
                SELECT ProductId, ProductName, SalePrice, IsActive
                FROM Products
                WHERE IsActive = 1 AND ProductId IN ({inClause});";

                await using var cmd = new SqlCommand(sql, conn);
                for (int i = 0; i < productIds.Count; i++)
                    cmd.Parameters.Add("@p" + i, SqlDbType.Int).Value = productIds[i];

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var p = new Product
                    {
                        ProductId = rd.GetInt32(0),
                        ProductName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        SalePrice = rd.GetDecimal(2),
                        IsActive = rd.GetBoolean(3)
                    };
                    dbProducts[p.ProductId] = p;
                }
            }

            if (dbProducts.Count != productIds.Count)
                return BadRequest(new { message = "One or more products are no longer available." });

            // ── 4. Compute totals ────────────────────────────────
            decimal subTotal = 0;
            var lineItems = new List<(int ProductId, decimal Qty, decimal UnitPrice, decimal Discount, decimal Total)>();

            foreach (var item in req.Items)
            {
                if (item.Quantity <= 0)
                    return BadRequest(new { message = "Quantity must be greater than zero." });

                if (!dbProducts.TryGetValue(item.ProductId, out var p))
                    return BadRequest(new { message = $"Product {item.ProductId} not found." });

                var gross = item.Quantity * p.SalePrice;
                var lineDisc = item.Discount < 0 ? 0 : Math.Min(item.Discount, gross);
                var lineTotal = Math.Max(0, gross - lineDisc);

                subTotal += gross;
                lineItems.Add((p.ProductId, item.Quantity, p.SalePrice, lineDisc, lineTotal));
            }

            var grandDiscount = Math.Max(0, Math.Min(req.Discount, subTotal));
            var grandTotal = subTotal - grandDiscount;

            var allowedMethods = new[] { "Cash", "Card", "EasyPaisa", "JazzCash", "Bank", "Other" };
            var paymentMethod = string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Cash" : req.PaymentMethod.Trim();
            if (!allowedMethods.Contains(paymentMethod)) paymentMethod = "Cash";

            var paidAmount = Math.Max(0, req.PaidAmount);
            var remaining = Math.Max(0, grandTotal - paidAmount);

            // ── 5. Generate invoice no inside the same connection ──
            //    (Must be inside the transaction to avoid race conditions)
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            try
            {
                // 5a. Invoice number
                var invoiceNo = await GenerateInvoiceNoAsync(tx);

                // 5b. Insert Sale
                int saleId;
                {
                    const string sql = @"
                    INSERT INTO Sales
                        (InvoiceNo, SaleDate, SubTotal, Discount, GrandTotal,
                         PaidAmount, RemainingAmount, PaymentMethod, UserId, CreatedDate)
                    OUTPUT INSERTED.SaleId
                    VALUES
                        (@inv, GETDATE(), @sub, @disc, @grand,
                         @paid, @remain, @method, @uid, GETDATE());";

                    await using var cmd = new SqlCommand(sql, conn, tx);
                    cmd.Parameters.Add("@inv", SqlDbType.VarChar, 30).Value = invoiceNo;
                    cmd.Parameters.Add("@sub", SqlDbType.Decimal).Value = subTotal;
                    cmd.Parameters.Add("@disc", SqlDbType.Decimal).Value = grandDiscount;
                    cmd.Parameters.Add("@grand", SqlDbType.Decimal).Value = grandTotal;
                    cmd.Parameters.Add("@paid", SqlDbType.Decimal).Value = paidAmount;
                    cmd.Parameters.Add("@remain", SqlDbType.Decimal).Value = remaining;
                    cmd.Parameters.Add("@method", SqlDbType.VarChar, 30).Value = paymentMethod;
                    cmd.Parameters.Add("@uid", SqlDbType.Int).Value = userId;

                    saleId = (int)(await cmd.ExecuteScalarAsync())!;
                }

                // 5c. Insert each SaleDetail + InventoryTransaction
                foreach (var li in lineItems)
                {
                    {
                        const string sql = @"
                        INSERT INTO SaleDetails
                            (SaleId, ProductId, Quantity, UnitPrice, Discount, TotalAmount)
                        VALUES
                            (@sid, @pid, @qty, @price, @disc, @total);";

                        await using var cmd = new SqlCommand(sql, conn, tx);
                        cmd.Parameters.Add("@sid", SqlDbType.Int).Value = saleId;
                        cmd.Parameters.Add("@pid", SqlDbType.Int).Value = li.ProductId;
                        cmd.Parameters.Add("@qty", SqlDbType.Decimal).Value = li.Qty;
                        cmd.Parameters.Add("@price", SqlDbType.Decimal).Value = li.UnitPrice;
                        cmd.Parameters.Add("@disc", SqlDbType.Decimal).Value = li.Discount;
                        cmd.Parameters.Add("@total", SqlDbType.Decimal).Value = li.Total;

                        await cmd.ExecuteNonQueryAsync();
                    }

                    {
                        var remarks = "Sold via invoice " + invoiceNo;
                        if (remarks.Length > 200) remarks = remarks.Substring(0, 200);

                        const string sql = @"
                        INSERT INTO InventoryTransactions
                            (ProductId, TransactionType, SaleId, QuantityIn, QuantityOut,
                             TransactionDate, Remarks, UserId, CreatedDate)
                        VALUES
                            (@pid, 'Sale', @sid, 0, @qty,
                             GETDATE(), @remarks, @uid, GETDATE());";

                        await using var cmd = new SqlCommand(sql, conn, tx);
                        cmd.Parameters.Add("@pid", SqlDbType.Int).Value = li.ProductId;
                        cmd.Parameters.Add("@sid", SqlDbType.Int).Value = saleId;
                        cmd.Parameters.Add("@qty", SqlDbType.Decimal).Value = li.Qty;
                        cmd.Parameters.Add("@remarks", SqlDbType.VarChar, 200).Value = remarks;
                        cmd.Parameters.Add("@uid", SqlDbType.Int).Value = userId;

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                await tx.CommitAsync();

                return Ok(new
                {
                    saleId,
                    invoiceNo,
                    grandTotal,
                    paid = paidAmount,
                    remaining
                });
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(); } catch { }

                return StatusCode(500, new
                {
                    message = "Checkout failed.",
                    inner = ex.Message
                });
            }
        }

        // ─────────────────────────────────────────────────────────
        // GET: /Pos/Receipt/5  →  printable thermal receipt
        // ─────────────────────────────────────────────────────────
        public async Task<IActionResult> Receipt(int id)
        {
            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();

            // Load sale + cashier name
            Sale? sale = null;
            string cashierName = "—";

            {
                const string sql = @"
                SELECT s.SaleId, s.InvoiceNo, s.SaleDate, s.SubTotal, s.Discount,
                       s.GrandTotal, s.PaidAmount, s.RemainingAmount, s.PaymentMethod,
                       s.UserId, s.CreatedDate, u.FullName
                FROM Sales s
                INNER JOIN Users u ON u.UserId = s.UserId
                WHERE s.SaleId = @id;";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;

                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    sale = new Sale
                    {
                        SaleId = rd.GetInt32(0),
                        InvoiceNo = rd.GetString(1),
                        SaleDate = rd.GetDateTime(2),
                        SubTotal = rd.GetDecimal(3),
                        Discount = rd.GetDecimal(4),
                        GrandTotal = rd.GetDecimal(5),
                        PaidAmount = rd.GetDecimal(6),
                        RemainingAmount = rd.GetDecimal(7),
                        PaymentMethod = rd.GetString(8),
                        UserId = rd.GetInt32(9),
                        CreatedDate = rd.GetDateTime(10)
                    };
                    cashierName = rd.IsDBNull(11) ? "—" : rd.GetString(11);
                }
            }

            if (sale == null) return NotFound();

            // Load details with product names
            var productNames = new Dictionary<int, string>();

            {
                const string sql = @"
                SELECT d.SaleDetailId, d.SaleId, d.ProductId, d.Quantity,
                       d.UnitPrice, d.Discount, d.TotalAmount, p.ProductName
                FROM SaleDetails d
                INNER JOIN Products p ON p.ProductId = d.ProductId
                WHERE d.SaleId = @id
                ORDER BY d.SaleDetailId;";

                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;

                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    sale.Details.Add(new SaleDetail
                    {
                        SaleDetailId = rd.GetInt32(0),
                        SaleId = rd.GetInt32(1),
                        ProductId = rd.GetInt32(2),
                        Quantity = rd.GetDecimal(3),
                        UnitPrice = rd.GetDecimal(4),
                        Discount = rd.GetDecimal(5),
                        TotalAmount = rd.GetDecimal(6)
                    });
                    productNames[rd.GetInt32(2)] = rd.IsDBNull(7) ? "" : rd.GetString(7);
                }
            }

            ViewBag.Cashier = cashierName;
            ViewBag.ProductNames = productNames;

            return View(sale);
        }

        // ─────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────
        private async Task<string> GenerateInvoiceNoAsync(SqlTransaction? tx = null)
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var prefix = $"INV-{today}-";

            await using var conn = tx != null
                ? tx.Connection!
                : new SqlConnection(_connStr);

            if (tx == null) await conn.OpenAsync();

            const string sql = @"
            SELECT TOP 1 InvoiceNo
            FROM Sales
            WHERE InvoiceNo LIKE @prefix + '%'
            ORDER BY SaleId DESC;";

            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@prefix", SqlDbType.VarChar, 30).Value = prefix;

            var last = await cmd.ExecuteScalarAsync() as string;

            int next = 1;
            if (!string.IsNullOrEmpty(last))
            {
                var tail = last.Substring(prefix.Length);
                if (int.TryParse(tail, out var n)) next = n + 1;
            }

            return prefix + next.ToString("D4");
        }
    }

    //[Authorize]
    //public class PosController : Controller
    //{
    //    private readonly AppDbContext _db;

    //    public PosController(AppDbContext db) => _db = db;

    //    // GET: /Pos  → loads all active products + next invoice no
    //    public async Task<IActionResult> Index()
    //    {
    //        var products = await _db.Products
    //            .Where(p => p.IsActive)
    //            .Join(_db.Categories,
    //                  p => p.CategoryId, c => c.CategoryId,
    //                  (p, c) => new PosProductDto
    //                  {
    //                      ProductId = p.ProductId,
    //                      ProductName = p.ProductName,
    //                      Barcode = p.Barcode,
    //                      SalePrice = p.SalePrice,
    //                      Unit = p.Unit,
    //                      CategoryName = c.CategoryName
    //                  })
    //            .OrderBy(p => p.ProductName)
    //            .ToListAsync();

    //        var vm = new PosViewModel
    //        {
    //            InvoiceNo = await GenerateInvoiceNoAsync(),
    //            Products = products
    //        };
    //        return View(vm);
    //    }

    //    // POST: /Pos/Checkout
    //    [HttpPost]
    //    [ValidateAntiForgeryToken]
    //    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req)
    //    {
    //        if (req == null || req.Items == null || req.Items.Count == 0)
    //            return BadRequest(new { message = "Cart is empty." });

    //        if (req.PaidAmount < 0 || req.Discount < 0)
    //            return BadRequest(new { message = "Invalid amounts." });

    //        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
    //        if (string.IsNullOrEmpty(userIdClaim))
    //            return Unauthorized(new { message = "User not authenticated." });

    //        var userId = int.Parse(userIdClaim);

    //        var userExists = await _db.Users.AnyAsync(u => u.UserId == userId && u.IsActive);
    //        if (!userExists)
    //            return Unauthorized(new { message = "Your session is no longer valid. Please log in again." });

    //        // ── Product fetch ────────────────────────────────────────
    //        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();

    //        // ✅ FIX: fetch to list first, then build dictionary in memory
    //        var dbProductsList = await _db.Products
    //            .Where(p => productIds.Contains(p.ProductId) && p.IsActive)
    //            .ToListAsync();

    //        var dbProducts = dbProductsList.ToDictionary(p => p.ProductId);

    //        if (dbProducts.Count != productIds.Count)
    //            return BadRequest(new { message = "One or more products are no longer available." });

    //        // ── Totals ───────────────────────────────────────────────
    //        decimal subTotal = 0;
    //        var details = new List<SaleDetail>();

    //        foreach (var item in req.Items)
    //        {
    //            if (item.Quantity <= 0)
    //                return BadRequest(new { message = "Quantity must be greater than zero." });

    //            if (!dbProducts.TryGetValue(item.ProductId, out var p))
    //                return BadRequest(new { message = $"Product {item.ProductId} not found." });

    //            var lineGross = item.Quantity * p.SalePrice;
    //            var lineDisc = item.Discount < 0 ? 0 : Math.Min(item.Discount, lineGross);
    //            var lineTotal = Math.Max(0, lineGross - lineDisc);

    //            subTotal += lineGross;

    //            details.Add(new SaleDetail
    //            {
    //                ProductId = p.ProductId,
    //                Quantity = item.Quantity,
    //                UnitPrice = p.SalePrice,
    //                Discount = lineDisc,
    //                TotalAmount = lineTotal
    //            });
    //        }

    //        var grandDiscount = Math.Max(0, Math.Min(req.Discount, subTotal));
    //        var grandTotal = subTotal - grandDiscount;

    //        var allowedMethods = new[] { "Cash", "Card", "EasyPaisa", "JazzCash", "Bank", "Other" };
    //        var paymentMethod = string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Cash" : req.PaymentMethod.Trim();
    //        if (!allowedMethods.Contains(paymentMethod)) paymentMethod = "Cash";

    //        var paidAmount = Math.Max(0, req.PaidAmount);
    //        var remaining = Math.Max(0, grandTotal - paidAmount);

    //        // ── Save ─────────────────────────────────────────────────
    //        await using var tx = await _db.Database.BeginTransactionAsync();

    //        try
    //        {
    //            var sale = new Sale
    //            {
    //                InvoiceNo = await GenerateInvoiceNoAsync(),
    //                SaleDate = DateTime.Now,
    //                SubTotal = subTotal,
    //                Discount = grandDiscount,
    //                GrandTotal = grandTotal,
    //                PaidAmount = paidAmount,
    //                RemainingAmount = remaining,
    //                PaymentMethod = paymentMethod,
    //                UserId = userId,
    //                CreatedDate = DateTime.Now
    //            };

    //            _db.Sales.Add(sale);
    //            await _db.SaveChangesAsync();

    //            foreach (var d in details)
    //            {
    //                d.SaleId = sale.SaleId;
    //                _db.SaleDetails.Add(d);

    //                var remarks = "Sold via invoice " + sale.InvoiceNo;
    //                if (remarks.Length > 200) remarks = remarks.Substring(0, 200);

    //                _db.InventoryTransactions.Add(new InventoryTransaction
    //                {
    //                    ProductId = d.ProductId,
    //                    TransactionType = "Sale",
    //                    SaleId = sale.SaleId,
    //                    QuantityIn = 0,
    //                    QuantityOut = d.Quantity,
    //                    TransactionDate = DateTime.Now,
    //                    Remarks = remarks,
    //                    UserId = userId,
    //                    CreatedDate = DateTime.Now
    //                });
    //            }

    //            await _db.SaveChangesAsync();
    //            await tx.CommitAsync();

    //            return Ok(new
    //            {
    //                saleId = sale.SaleId,
    //                invoiceNo = sale.InvoiceNo,
    //                grandTotal,
    //                paid = paidAmount,
    //                remaining
    //            });
    //        }
    //        catch (Exception ex)
    //        {
    //            try { await tx.RollbackAsync(); } catch { }

    //            var inner = ex.InnerException?.Message ?? "";

    //            var stage = "unknown";
    //            if (inner.Contains("CK_Sales_Amounts", StringComparison.OrdinalIgnoreCase)) stage = "Sales CHECK: amounts";
    //            else if (inner.Contains("CK_Sales_Discount", StringComparison.OrdinalIgnoreCase)) stage = "Sales CHECK: discount";
    //            else if (inner.Contains("CK_Sales_PaymentMethod", StringComparison.OrdinalIgnoreCase)) stage = "Sales CHECK: payment method";
    //            else if (inner.Contains("CK_SaleDetails", StringComparison.OrdinalIgnoreCase)) stage = "SaleDetails CHECK";
    //            else if (inner.Contains("CK_InventoryTransactions", StringComparison.OrdinalIgnoreCase)) stage = "InventoryTransactions CHECK";
    //            else if (inner.Contains("FK_Sales_Users", StringComparison.OrdinalIgnoreCase)) stage = "FK: Sales → Users";
    //            else if (inner.Contains("FK_SaleDetails_Sales", StringComparison.OrdinalIgnoreCase)) stage = "FK: SaleDetails → Sales";
    //            else if (inner.Contains("FK_SaleDetails_Products", StringComparison.OrdinalIgnoreCase)) stage = "FK: SaleDetails → Products";
    //            else if (inner.Contains("FK_InventoryTransactions", StringComparison.OrdinalIgnoreCase)) stage = "FK: InventoryTransactions";
    //            else if (inner.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)) stage = "UNIQUE constraint";
    //            else if (inner.Contains("String or binary data would be truncated", StringComparison.OrdinalIgnoreCase)) stage = "COLUMN too small";
    //            else if (inner.Contains("Cannot insert the value NULL", StringComparison.OrdinalIgnoreCase)) stage = "NULL not allowed";
    //            else if (inner.Contains("Incorrect syntax near the keyword 'WITH'", StringComparison.OrdinalIgnoreCase)) stage = "EF SQL generation (Contains/ToDictionary)";

    //            return StatusCode(500, new
    //            {
    //                message = "Checkout failed.",
    //                stage,
    //                inner,
    //                payload = new { userId, paymentMethod, subTotal, grandDiscount, grandTotal, paidAmount, remaining }
    //            });
    //        }
    //    }

    //    // GET: /Pos/Receipt/5  → printable receipt view (thermal-friendly)
    //    public async Task<IActionResult> Receipt(int id)
    //    {
    //        var sale = await _db.Sales
    //            .Include(s => s.Details)
    //            .FirstOrDefaultAsync(s => s.SaleId == id);

    //        if (sale == null) return NotFound();

    //        var userId = sale.UserId;
    //        var cashier = await _db.Users.FindAsync(userId);

    //        var productNames = await _db.Products
    //            .Where(p => sale.Details.Select(d => d.ProductId).Contains(p.ProductId))
    //            .ToDictionaryAsync(p => p.ProductId, p => p.ProductName);

    //        ViewBag.Cashier = cashier?.FullName ?? "—";
    //        ViewBag.ProductNames = productNames;

    //        return View(sale);
    //    }

    //    private async Task<string> GenerateInvoiceNoAsync()
    //    {
    //        var today = DateTime.Now.ToString("yyyyMMdd");
    //        var prefix = $"INV-{today}-";
    //        var lastNo = await _db.Sales
    //            .Where(s => s.InvoiceNo.StartsWith(prefix))
    //            .OrderByDescending(s => s.SaleId)
    //            .Select(s => s.InvoiceNo)
    //            .FirstOrDefaultAsync();

    //        int next = 1;
    //        if (lastNo != null)
    //        {
    //            var tail = lastNo.Substring(prefix.Length);
    //            if (int.TryParse(tail, out var n)) next = n + 1;
    //        }
    //        return prefix + next.ToString("D4");
    //    }
    //}



}
