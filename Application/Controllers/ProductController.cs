using Application.Data;
using Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Application.Controllers
{
    [Authorize]
    public class ProductController : Controller
    {
        private readonly AppDbContext _db;

        public ProductController(AppDbContext db) => _db = db;

        // GET: /Product
        public async Task<IActionResult> Index()
        {
            var products = await _db.Products
                .OrderByDescending(p => p.ProductId)
                .ToListAsync();

            var categoryNames = await _db.Categories
                .ToDictionaryAsync(c => c.CategoryId, c => c.CategoryName);

            var vm = new ProductListViewModel
            {
                Products = products,
                CategoryNames = categoryNames
            };

            return View(vm);
        }

        // GET: /Product/Create
        public async Task<IActionResult> Create()
        {
            var vm = new ProductFormViewModel
            {
                Product = new Product(),
                Categories = await GetCategorySelectListAsync()
            };

            if (!vm.Categories.Any())
            {
                TempData["Error"] = "Please add a category first before adding products.";
                return RedirectToAction("Index", "Category");
            }

            return View(vm);
        }

        // POST: /Product/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProductFormViewModel vm)
        {
            // Trim / nullify empty barcode
            vm.Product.Barcode = string.IsNullOrWhiteSpace(vm.Product.Barcode)
                ? null
                : vm.Product.Barcode.Trim();

            // Duplicate barcode check (only when provided)
            if (vm.Product.Barcode != null)
            {
                bool barcodeExists = await _db.Products
                    .AnyAsync(p => p.Barcode == vm.Product.Barcode);

                if (barcodeExists)
                    ModelState.AddModelError("Product.Barcode", "This barcode is already used by another product.");
            }

            if (!ModelState.IsValid)
            {
                vm.Categories = await GetCategorySelectListAsync();
                return View(vm);
            }

            vm.Product.CreatedDate = DateTime.Now;
            _db.Products.Add(vm.Product);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Product added successfully.";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Product/Edit/5
        public async Task<IActionResult> Edit(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            var vm = new ProductFormViewModel
            {
                Product = product,
                Categories = await GetCategorySelectListAsync()
            };
            return View(vm);
        }

        // POST: /Product/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, ProductFormViewModel vm)
        {
            if (id != vm.Product.ProductId) return BadRequest();

            vm.Product.Barcode = string.IsNullOrWhiteSpace(vm.Product.Barcode)
                ? null
                : vm.Product.Barcode.Trim();

            if (vm.Product.Barcode != null)
            {
                bool barcodeExists = await _db.Products
                    .AnyAsync(p => p.Barcode == vm.Product.Barcode && p.ProductId != id);

                if (barcodeExists)
                    ModelState.AddModelError("Product.Barcode", "Another product already uses this barcode.");
            }

            if (!ModelState.IsValid)
            {
                vm.Categories = await GetCategorySelectListAsync();
                return View(vm);
            }

            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.ProductName = vm.Product.ProductName;
            product.CategoryId = vm.Product.CategoryId;
            product.Barcode = vm.Product.Barcode;
            product.SalePrice = vm.Product.SalePrice;
            product.PurchasePrice = vm.Product.PurchasePrice;
            product.Unit = vm.Product.Unit;
            product.IsActive = vm.Product.IsActive;

            await _db.SaveChangesAsync();

            TempData["Success"] = "Product updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /Product/ToggleActive/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsActive = !product.IsActive;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Product marked {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /Product/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            _db.Products.Remove(product);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Product deleted.";
            return RedirectToAction(nameof(Index));
        }

        // Helper — loads active categories for the dropdown
        private async Task<List<SelectListItem>> GetCategorySelectListAsync()
        {
            return await _db.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.CategoryName)
                .Select(c => new SelectListItem
                {
                    Value = c.CategoryId.ToString(),
                    Text = c.CategoryName
                })
                .ToListAsync();
        }
    }
}
