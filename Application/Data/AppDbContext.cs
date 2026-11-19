using Application.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace Application.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Sale> Sales { get; set; }
        public DbSet<SaleDetail> SaleDetails { get; set; }
        public DbSet<InventoryTransaction> InventoryTransactions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Our SQL column is TransactionId, but the entity class is InventoryTransaction.
            // EF Core expects "Id" or "InventoryTransactionId" by convention, so tell it explicitly.
            modelBuilder.Entity<InventoryTransaction>()
                .HasKey(t => t.TransactionId);
        }
    }
}
