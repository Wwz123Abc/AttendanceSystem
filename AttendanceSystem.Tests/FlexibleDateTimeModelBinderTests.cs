using AttendanceSystem.ModelBinding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 2026-11-23：DateOnly/TimeOnly 的容错模型绑定（不依赖浏览器原生控件提交的固定格式）。
/// </summary>
public class FlexibleDateTimeModelBinderTests
{
    private sealed class SingleValueProvider(string key, string value) : IValueProvider
    {
        public bool ContainsPrefix(string prefix) => true;
        public ValueProviderResult GetValue(string name) =>
            name == key ? new ValueProviderResult(value) : ValueProviderResult.None;
    }

    private static ModelBindingContext CreateContext(Type type, string name, string value)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(type);
        var actionContext = new ActionContext(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        return new DefaultModelBindingContext
        {
            ActionContext = actionContext,
            ModelMetadata = metadata,
            ModelName = name,
            ValueProvider = new SingleValueProvider(name, value),
            ModelState = new ModelStateDictionary()
        };
    }

    [Theory]
    [InlineData("2026-11-23", 2026, 11, 23)]
    [InlineData("20261123", 2026, 11, 23)]
    [InlineData("11-23", 2026, 11, 23)]
    public async Task DateOnly接受多种写法(string raw, int y, int mo, int d)
    {
        var ctx = CreateContext(typeof(DateOnly), "d", raw);
        var binder = new FlexibleDateTimeModelBinder(FlexibleDateTimeModelBinder.ValueKind.Date, nullable: false);
        await binder.BindModelAsync(ctx);
        Assert.Equal(new DateOnly(y, mo, d), Assert.IsType<DateOnly>(ctx.Result.Model));
    }

    [Theory]
    [InlineData("20:30", 20, 30)]
    [InlineData("2030", 20, 30)]
    [InlineData("930", 9, 30)]
    public async Task TimeOnly接受多种写法(string raw, int h, int mi)
    {
        var ctx = CreateContext(typeof(TimeOnly), "t", raw);
        var binder = new FlexibleDateTimeModelBinder(FlexibleDateTimeModelBinder.ValueKind.Time, nullable: false);
        await binder.BindModelAsync(ctx);
        Assert.Equal(new TimeOnly(h, mi), Assert.IsType<TimeOnly>(ctx.Result.Model));
    }

    [Fact]
    public async Task 可空日期时间为空时绑定为null()
    {
        var ctx = CreateContext(typeof(DateOnly?), "d", "");
        var binder = new FlexibleDateTimeModelBinder(FlexibleDateTimeModelBinder.ValueKind.Date, nullable: true);
        await binder.BindModelAsync(ctx);
        Assert.True(ctx.Result.IsModelSet);
        Assert.Null(ctx.Result.Model);
    }
}
