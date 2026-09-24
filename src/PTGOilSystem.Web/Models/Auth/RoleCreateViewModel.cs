using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Auth;

public class RoleCreateViewModel
{
    [Required(ErrorMessage = "نام نقش اجباری است.")]
    [MaxLength(50, ErrorMessage = "نام نقش نباید بیشتر از 50 کاراکتر باشد.")]
    [Display(Name = "نام نقش")]
    public string Name { get; set; } = "";

    [MaxLength(200, ErrorMessage = "توضیح نباید بیشتر از 200 کاراکتر باشد.")]
    [Display(Name = "توضیح")]
    public string? Description { get; set; }

    [Display(Name = "اجازه ثبت و ویرایش اطلاعات")]
    public bool CanManageData { get; set; }

    [Display(Name = "اجازه مدیریت کاربران و نقش‌ها")]
    public bool CanManageUsers { get; set; }

    [Display(Name = "بخش‌های قابل نمایش")]
    public string[] AllowedNavigationItems { get; set; } = [];

    [Display(Name = "دسترسی‌های ویژه")]
    public string[] GrantedPermissions { get; set; } = [];
}
