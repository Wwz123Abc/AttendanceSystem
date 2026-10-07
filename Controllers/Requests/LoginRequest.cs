using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using AttendanceSystem.Helpers;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Controllers;

// record = 一种简洁的“只读数据载体”，这里用来装请求传来的字段
public record LoginRequest(string EmployeeNo, string Password, bool RememberMe = false);
