using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyIsSystemOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemOwner",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // اگر و فقط اگر دقیقاً یک شرکت وجود داشته باشد، همان به‌عنوان مالکِ سیستم علامت می‌خورد.
            // اگر صفر یا بیش از یک شرکت باشد، این مهاجرت هیچ مالکی را خودسرانه انتخاب نمی‌کند؛ تعیینِ
            // مالک در آن حالت تصمیمِ صریحِ کاربر است (فیلد IsSystemOwner دستی مقداردهی شود).
            migrationBuilder.Sql(
                "UPDATE \"Companies\" SET \"IsSystemOwner\" = TRUE " +
                "WHERE (SELECT COUNT(*) FROM \"Companies\") = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_IsSystemOwner",
                table: "Companies",
                column: "IsSystemOwner",
                unique: true,
                filter: "\"IsSystemOwner\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_IsSystemOwner",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "IsSystemOwner",
                table: "Companies");
        }
    }
}
