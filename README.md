# ComnetSolution.AutoMapper

[![NuGet](https://img.shields.io/nuget/v/THGEchoSystem.Mapper.svg)](https://www.nuget.org/packages/THGEchoSystem.Mapper)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-6%20%7C%207%20%7C%208%20%7C%209-blue)](https://dotnet.microsoft.com)

A high-performance, open-source object-to-object mapper for .NET built on **compiled LINQ expression trees**.  
Designed as a **drop-in replacement for AutoMapper** — migrate existing projects with minimal code changes.

---

## Why ComnetSolution.AutoMapper?

AutoMapper became closed-source / commercial in recent versions. This library replicates the AutoMapper API surface you rely on — `Profile`, `CreateMap`, `ForMember`, `ReverseMap`, `MapList`, `IMapper` injection — without any licensing restrictions and with zero external dependencies beyond `Microsoft.Extensions.DependencyInjection.Abstractions`.

**Key benefits:**
- 🚀 Compiled expression trees — reflection cost paid **once**, subsequent maps run at near-native speed
- 🔁 Drop-in API — rename `AutoMapper.Profile` → `MapperProfile`, `IMapper` → `IMapper`
- 🔗 Full DI integration — `AddMapper(...)` or auto-discover profiles with `AddMapperFromAssemblies(...)`
- 📦 Multi-target — supports .NET 6, 7, 8 and 10
- 🛡️ MIT licensed — free for commercial and open-source use

---

## Installation

```bash
dotnet add package ComnetSolution.AutoMapper
```

or via NuGet Package Manager:

```
Install-Package ComnetSolution.AutoMapper
```

---

## Quick Start

### 1 — Define a Profile

```csharp
using ComnetMapper.Configuration;

public class OrderProfile : MapperProfile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer.FullName))
            .ForMember(d => d.InternalNote, o => o.Ignore())   // skip this property
            .ReverseMap();                                       // also maps Dto → Order
    }
}
```

### 2 — Register in DI (Program.cs / Startup.cs)

```csharp
// Option A: list profiles explicitly
builder.Services.AddMapper(mapper =>
{
    mapper.AddProfile<OrderProfile>();
    mapper.AddProfile<CustomerProfile>();
});

// Option B: auto-discover every MapperProfile in the assembly
builder.Services.AddMapperFromAssemblies(typeof(Program).Assembly);
```

### 3 — Inject and Use

```csharp
public class OrderService(IMapper mapper)
{
    public OrderDto GetOrder(int id)
    {
        var order = _repo.FindById(id);
        return mapper.Map<OrderDto>(order);   // single object
    }

    public List<OrderDto> GetAll()
    {
        var orders = _repo.GetAll();
        return mapper.MapList<Order, OrderDto>(orders).ToList();
    }
}
```

---

## API Reference

### `MapperProfile`

| Method | Description |
|---|---|
| `CreateMap<TSource, TDest>()` | Registers a source→destination mapping |
| `.ForMember(d => d.Prop, o => o.MapFrom(...))` | Custom source expression for a member |
| `.ForMember(d => d.Prop, o => o.Ignore())` | Exclude a destination member from mapping |
| `.BeforeMap((src, dest, mapper) => ...)` | Action to run before property assignment |
| `.AfterMap((src, dest, mapper) => ...)` | Action to run after property assignment |
| `.ReverseMap()` | Also registers the inverse (TDest → TSource) map |

### `IMapper`

| Method | Description |
|---|---|
| `Map<TDest>(object source)` | Map to a new TDest instance |
| `Map<TSource, TDest>(source, destination)` | Map onto an existing TDest instance |
| `MapList<TSource, TDest>(IEnumerable<TSource>)` | Map a collection |
| `TryMap<TDest>(source, out MapResult<TDest>)` | Safe mapping — returns false + error instead of throwing |

### DI Registration

| Extension | Description |
|---|---|
| `AddMapper(Action<Mapper>)` | Manual profile registration |
| `AddMapperFromAssemblies(params Assembly[])` | Auto-discover all profiles in assemblies |

---

## Advanced Examples

### Before/After Map Hooks

```csharp
CreateMap<User, UserDto>()
    .BeforeMap((src, dest, mapper) =>
    {
        // Runs before any property is set — useful for validation
        if (src.IsDeleted) throw new InvalidOperationException("Cannot map deleted user");
    })
    .AfterMap((src, dest, mapper) =>
    {
        // Runs after all properties — useful for computed values
        dest.DisplayName = $"{dest.FirstName} {dest.LastName}".Trim();
    });
```

### Safe Mapping with TryMap

```csharp
if (mapper.TryMap<OrderDto>(order, out var result))
{
    return result.Value;
}
else
{
    _logger.LogError("Mapping failed: {Error}", result.Error);
    return null;
}
```

### Auto-Discover Profiles

```csharp
// Scans the entire application assembly for MapperProfile subclasses
builder.Services.AddMapperFromAssemblies(
    typeof(Program).Assembly,
    typeof(SharedProfile).Assembly   // include other assemblies if needed
);
```

### Reverse Map

```csharp
CreateMap<CreateOrderRequest, Order>().ReverseMap();

// Now both directions work:
var order = mapper.Map<Order>(request);
var request = mapper.Map<CreateOrderRequest>(order);
```

---

## Migration from AutoMapper

| AutoMapper | ComnetMapper |
|---|---|
| `using AutoMapper;` | `using ComnetMapper.Configuration;` |
| `Profile` | `MapperProfile` |
| `IMapper` | `IMapper` |
| `services.AddAutoMapper(...)` | `services.AddMapper(...)` |
| `mapper.Map<TDest>(source)` | ✅ same |
| `mapper.Map<TSource, TDest>(source, dest)` | ✅ same |
| `CreateMap<A, B>()` | ✅ same |
| `ForMember(d => d.X, o => o.MapFrom(...))` | ✅ same |
| `ForMember(d => d.X, o => o.Ignore())` | ✅ same |
| `.ReverseMap()` | ✅ same |

---

## Supported Type Conversions

The mapper handles the following automatically (no configuration needed):

- Same-type assignment
- Widening: `T → T?` (e.g. `int → int?`)
- Narrowing: `T? → T` (returns `default` when null)
- Numeric conversions (`int → long`, `float → double`, etc.)
- Enum ↔ enum, enum ↔ numeric
- Implicit / explicit conversion operators
- `IEnumerable<TSrc>` → `IList<TDest>` (element-wise mapping)
- Nested complex objects (recursively mapped)

---

## Project Structure

```
ComnetMapper/
├── src/
│   └── ComnetMapper/
│       ├── Abstractions/
│       │   └── IMapper.cs          # Public interface + MapResult<T>
│       ├── Configuration/
│       │   └── MapperProfile.cs          # Profile base class + fluent API
│       ├── Core/
│       │   └── Mapper.cs           # Engine: expression compilation + caching
│       ├── Extensions/
│       │   └── MapperServiceExtensions.cs  # IServiceCollection extensions
│       │   └── MapperExtensions.cs  # for common methods.
│       └── ComnetMapper.csproj
├── README.md
└── LICENSE
```

---

## Contributing

Contributions are welcome! Please open an issue or pull request on GitHub.

1. Fork the repo
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Commit your changes and push
4. Open a pull request against `main`

---

## License

MIT — see [LICENSE](LICENSE) for full text.
