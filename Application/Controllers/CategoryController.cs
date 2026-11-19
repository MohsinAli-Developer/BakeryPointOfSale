using Application.Data;
using Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Application.Controllers
{
    [Authorize]
    public class CategoryController : Controller
    {
        private readonly AppDbContext _db;

        public CategoryController(AppDbContext db) => _db = db;

        // GET: /Category
        public async Task<IActionResult> Index()
        {
            var categories = await _db.Categories
                .OrderByDescending(c => c.CategoryId)
                .ToListAsync();
            return View(categories);
        }

        // GET: /Category/Create
        public IActionResult Create() => View(new Category());

        // POST: /Category/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Category model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Duplicate check
            bool exists = await _db.Categories
                .AnyAsync(c => c.CategoryName == model.CategoryName);

            if (exists)
            {
                ModelState.AddModelError("CategoryName", "This category already exists.");
                return View(model);
            }

            model.CreatedDate = DateTime.Now;
            _db.Categories.Add(model);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Category added successfully.";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Category/Edit/5
        public async Task<IActionResult> Edit(int id)
        {
            var category = await _db.Categories.FindAsync(id);
            if (category == null) return NotFound();
            return View(category);
        }

        // POST: /Category/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Category model)
        {
            if (id != model.CategoryId) return BadRequest();
            if (!ModelState.IsValid) return View(model);

            bool duplicate = await _db.Categories
                .AnyAsync(c => c.CategoryName == model.CategoryName && c.CategoryId != id);

            if (duplicate)
            {
                ModelState.AddModelError("CategoryName", "Another category with this name already exists.");
                return View(model);
            }

            var category = await _db.Categories.FindAsync(id);
            if (category == null) return NotFound();

            category.CategoryName = model.CategoryName;
            category.IsActive = model.IsActive;

            await _db.SaveChangesAsync();

            TempData["Success"] = "Category updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /Category/ToggleActive/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var category = await _db.Categories.FindAsync(id);
            if (category == null) return NotFound();

            category.IsActive = !category.IsActive;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Category marked {(category.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        // POST: /Category/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var category = await _db.Categories.FindAsync(id);
            if (category == null) return NotFound();

            _db.Categories.Remove(category);
            await _db.SaveChangesAsync();

            TempData["Success"] = "Category deleted.";
            return RedirectToAction(nameof(Index));
        }
    }
}
