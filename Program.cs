using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using BCrypt.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// 1. Регистрируем DbContext с SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
{
	options.UseSqlite("Data Source=nutshop.db");
});

// Для JSON-сериализации (чтобы избежать потенциальных циклических ссылок)
builder.Services.AddControllers().AddJsonOptions(opts =>
{
	opts.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// Подключаем Swagger (документация)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRazorPages();

var app = builder.Build();

// Подключаем статические файлы (css, js, изображения)
app.UseStaticFiles();

// 2. Создаём или мигрируем базу данных
using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	db.Database.EnsureCreated();

	// (Необязательно) Добавим несколько товаров-«орехов» в базу, если их нет
	if (!db.Products.Any())
	{
		db.Products.AddRange(new[]
		{
			new Product { Name = "Грецкий орех",  Price = 250.0m, ImageUrl = "/images/walnut.jpg" },
			new Product { Name = "Миндаль",       Price = 320.0m, ImageUrl = "/images/almond.jpg" },
			new Product { Name = "Фундук",        Price = 280.0m, ImageUrl = "/images/hazelnut.jpg" },
		});
		db.SaveChanges();
	}
}

// Swagger доступен, если среда Development
if (app.Environment.IsDevelopment())
{
	app.UseDeveloperExceptionPage();
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapRazorPages();

//
// ============================
//    ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
// ============================
//

// Извлечение Bearer-токена из заголовка Authorization
static string? ExtractBearerToken(string authHeader)
{
	if (string.IsNullOrEmpty(authHeader)) return null;
	var match = Regex.Match(authHeader, @"Bearer\s+(.*)", RegexOptions.IgnoreCase);
	if (match.Success) return match.Groups[1].Value.Trim();
	return null;
}

// Сохранение записи в историю запросов
void SaveRequestHistory(AppDbContext db, int userId, string endpoint)
{
	db.RequestHistories.Add(new RequestHistory
	{
		UserId = userId,
		Endpoint = endpoint,
		Timestamp = DateTime.UtcNow
	});
	db.SaveChanges();
}

// Получение текущего (авторизованного) пользователя на основе токена
async Task<User?> GetCurrentUser(AppDbContext db, HttpRequest req)
{
	var authHeader = req.Headers["Authorization"].ToString();
	var token = ExtractBearerToken(authHeader);
	if (string.IsNullOrWhiteSpace(token)) return null;

	var user = await db.Users.FirstOrDefaultAsync(u => u.Token == token);
	return user;
}

//
// ============================
//         ЭНДПОИНТЫ
// ============================
//

// Получение текущих данных пользователя (GET /api/personal)
app.MapGet("/api/personal", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null)
		return Results.Unauthorized();

	return Results.Ok(new
	{
		username = currentUser.Username,
		email = currentUser.Email,
		avatarUrl = currentUser.AvatarUrl,
		shippingAddress = currentUser.ShippingAddress,
		paymentMethod = currentUser.PaymentMethod,
		balance = currentUser.Balance
	});
});

// ---------- 1) Регистрация (POST /api/register) ----------
app.MapPost("/api/register", async (AppDbContext db, string username, string password, string email) =>
{
	if (string.IsNullOrWhiteSpace(username) ||
		string.IsNullOrWhiteSpace(password) ||
		string.IsNullOrWhiteSpace(email))
	{
		return Results.BadRequest(new { error = "Username, password и email обязательны" });
	}

	// Проверяем, нет ли уже пользователя с таким username или email
	var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Username == username || u.Email == email);
	if (existingUser != null)
		return Results.BadRequest(new { error = "Пользователь с таким именем или email уже существует" });

	string hash = BCrypt.Net.BCrypt.HashPassword(password);
	string token = AuthUtils.GenerateToken();

	var user = new User
	{
		Username = username,
		Email = email,
		PasswordHash = hash,
		Token = token,
		Balance = 0  // При регистрации баланс 0
	};
	db.Users.Add(user);
	await db.SaveChangesAsync();

	return Results.Created("/api/register", new
	{
		message = "Пользователь успешно зарегистрирован",
		token = user.Token
	});
});

// ---------- 2) Авторизация (логин) (POST /api/login) ----------
app.MapPost("/api/login", async (AppDbContext db, string username, string password) =>
{
	var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
	if (user == null)
		return Results.BadRequest(new { error = "Неверное имя пользователя или пароль" });

	// Проверяем пароль
	bool validPassword = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
	if (!validPassword)
		return Results.BadRequest(new { error = "Неверное имя пользователя или пароль" });

	// Генерируем новый токен (по желанию, можно оставлять прежний)
	user.Token = AuthUtils.GenerateToken();
	await db.SaveChangesAsync();

	return Results.Ok(new
	{
		message = "Успешный вход",
		token = user.Token
	});
});

// ---------- 3) Изменить имя пользователя (PATCH /api/change_username) ----------
app.MapMethods("/api/change_username", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string newUsername) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(newUsername))
		return Results.BadRequest(new { error = "Новое имя не может быть пустым" });

	// Проверяем, не занято ли новое имя
	var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == newUsername);
	if (existing != null)
		return Results.BadRequest(new { error = "Данное имя пользователя уже занято" });

	currentUser.Username = newUsername;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /change_username");
	return Results.Ok(new { message = "Имя пользователя обновлено" });
});

// ---------- 4) Изменить email пользователя (PATCH /api/change_email) ----------
app.MapMethods("/api/change_email", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string newEmail) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(newEmail))
		return Results.BadRequest(new { error = "Новый email не может быть пустым" });

	// Проверяем, не занят ли email
	var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == newEmail);
	if (existing != null)
		return Results.BadRequest(new { error = "Данный email уже используется" });

	currentUser.Email = newEmail;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /change_email");
	return Results.Ok(new { message = "Email обновлён" });
});

// ---------- 5) Изменить пароль (PATCH /api/change_password) ----------
app.MapMethods("/api/change_password", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string newPassword) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(newPassword))
		return Results.BadRequest(new { error = "Новый пароль не может быть пустым" });

	currentUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
	// Обновим токен, чтобы старый пароль не оставался валидным
	currentUser.Token = AuthUtils.GenerateToken();
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /change_password");
	return Results.Ok(new
	{
		message = "Пароль изменён",
		new_token = currentUser.Token
	});
});

// ---------- 6) Изменить аватар пользователя (PATCH /api/change_avatar) ----------
app.MapMethods("/api/change_avatar", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string newAvatarUrl) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(newAvatarUrl))
		return Results.BadRequest(new { error = "URL аватара не может быть пустым" });

	currentUser.AvatarUrl = newAvatarUrl;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /change_avatar");
	return Results.Ok(new { message = "Аватар обновлён" });
});

// ---------- 7) Посмотреть список всех товаров (GET /api/products) ----------
app.MapGet("/api/products", async (AppDbContext db) =>
{
	var products = await db.Products.ToListAsync();
	return Results.Ok(products.Select(p => new
	{
		p.Id,
		p.Name,
		p.Price,
		p.ImageUrl // Возвращаем путь/URL к картинке
	}));
});

// ---------- 8) Поиск товара по названию (GET /api/search?query=...) ----------
app.MapGet("/api/search", async (AppDbContext db, string query) =>
{
	if (string.IsNullOrWhiteSpace(query))
		return Results.BadRequest(new { error = "Параметр query не должен быть пустым" });

	var allProducts = await db.Products.ToListAsync();
	var results = allProducts
		.Where(p => p.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
		.ToList();

	return Results.Ok(results.Select(p => new
	{
		p.Id,
		p.Name,
		p.Price,
		p.ImageUrl
	}));
});

// ---------- 9) Добавить товар в корзину (POST /api/cart/add) ----------
app.MapPost("/api/cart/add", async (AppDbContext db, HttpRequest req, int productId, int quantity) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (quantity <= 0)
		return Results.BadRequest(new { error = "Количество должно быть > 0" });

	var product = await db.Products.FindAsync(productId);
	if (product == null)
		return Results.NotFound(new { error = "Товар не найден" });

	// Проверяем, есть ли уже этот товар в корзине
	var cartItem = await db.CartItems
		.FirstOrDefaultAsync(ci => ci.UserId == currentUser.Id && ci.ProductId == productId);

	if (cartItem == null)
	{
		cartItem = new CartItem
		{
			UserId = currentUser.Id,
			ProductId = productId,
			Quantity = quantity
		};
		db.CartItems.Add(cartItem);
	}
	else
	{
		cartItem.Quantity += quantity;
	}
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /cart/add");
	return Results.Ok(new
	{
		message = $"Добавлено {quantity} шт. товара '{product.Name}' в корзину"
	});
});

// ---------- 10) Удалить товар из корзины (POST /api/cart/remove) ----------
app.MapPost("/api/cart/remove", async (AppDbContext db, HttpRequest req, int productId, int quantity) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (quantity <= 0)
		return Results.BadRequest(new { error = "Количество должно быть > 0" });

	var cartItem = await db.CartItems
		.FirstOrDefaultAsync(ci => ci.UserId == currentUser.Id && ci.ProductId == productId);

	if (cartItem == null)
		return Results.NotFound(new { error = "Товара нет в корзине" });

	cartItem.Quantity -= quantity;
	if (cartItem.Quantity <= 0)
	{
		db.CartItems.Remove(cartItem);
	}
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /cart/remove");
	return Results.Ok(new
	{
		message = $"Удалено {quantity} шт. товара '{productId}' из корзины"
	});
});

// ---------- 11) Просмотреть содержимое корзины (GET /api/cart) ----------
app.MapGet("/api/cart", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var cartItems = await db.CartItems
		.Where(ci => ci.UserId == currentUser.Id)
		.ToListAsync();

	var result = new List<object>();
	foreach (var ci in cartItems)
	{
		var product = await db.Products.FindAsync(ci.ProductId);
		if (product == null) continue;

		result.Add(new
		{
			ProductId = product.Id,
			product.Name,
			product.Price,
			ci.Quantity,
			Subtotal = product.Price * ci.Quantity
		});
	}
	SaveRequestHistory(db, currentUser.Id, "GET /cart");
	return Results.Ok(result);
});

// ---------- 12) Очистить корзину (POST /api/cart/clear) ----------
app.MapPost("/api/cart/clear", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var cartItems = db.CartItems.Where(ci => ci.UserId == currentUser.Id);
	db.CartItems.RemoveRange(cartItems);
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /cart/clear");
	return Results.Ok(new { message = "Корзина очищена" });
});

// ---------- Обновить количество (POST /api/cart/updateQuantity) ----------
app.MapPost("/api/cart/updateQuantity", async (AppDbContext db, HttpRequest req, int productId, int newQuantity) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	// Можно запретить отрицательное число, а 0 — для удаления
	if (newQuantity < 0)
		return Results.BadRequest(new { error = "Количество не может быть меньше 0" });

	var cartItem = await db.CartItems
		.FirstOrDefaultAsync(ci => ci.UserId == currentUser.Id && ci.ProductId == productId);

	if (cartItem == null)
		return Results.NotFound(new { error = "Товар не найден в корзине" });

	if (newQuantity == 0)
	{
		// Если пользователь выставил 0 — удаляем товар из корзины
		db.CartItems.Remove(cartItem);
	}
	else
	{
		cartItem.Quantity = newQuantity;
	}
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /cart/updateQuantity");
	return Results.Ok(new { message = "Количество товара обновлено" });
});


// ---------- 13) Добавить товар в «Желаемое» (POST /api/wishlist/add) ----------
app.MapPost("/api/wishlist/add", async (AppDbContext db, HttpRequest req, int productId) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var product = await db.Products.FindAsync(productId);
	if (product == null)
		return Results.NotFound(new { error = "Товар не найден" });

	var wishItem = await db.WishlistItems
		.FirstOrDefaultAsync(w => w.UserId == currentUser.Id && w.ProductId == productId);

	if (wishItem != null)
		return Results.BadRequest(new { error = "Товар уже в списке желаемого" });

	db.WishlistItems.Add(new WishlistItem
	{
		UserId = currentUser.Id,
		ProductId = productId
	});
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /wishlist/add");
	return Results.Ok(new { message = $"Товар '{product.Name}' добавлен в Желаемое" });
});

// ---------- 14) Удалить товар из «Желаемое» (POST /api/wishlist/remove) ----------
app.MapPost("/api/wishlist/remove", async (AppDbContext db, HttpRequest req, int productId) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var wishItem = await db.WishlistItems
		.FirstOrDefaultAsync(w => w.UserId == currentUser.Id && w.ProductId == productId);

	if (wishItem == null)
		return Results.NotFound(new { error = "Товара нет в списке желаемого" });

	db.WishlistItems.Remove(wishItem);
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /wishlist/remove");
	return Results.Ok(new { message = $"Товар (ID={productId}) удалён из Желаемого" });
});

// ---------- 15) Посмотреть «Желаемое» (GET /api/wishlist) ----------
app.MapGet("/api/wishlist", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var wishItems = await db.WishlistItems
		.Where(w => w.UserId == currentUser.Id)
		.ToListAsync();

	var result = new List<object>();
	foreach (var wi in wishItems)
	{
		var product = await db.Products.FindAsync(wi.ProductId);
		if (product == null) continue;

		result.Add(new
		{
			ProductId = product.Id,
			product.Name,
			product.Price,
			product.ImageUrl
		});
	}

	SaveRequestHistory(db, currentUser.Id, "GET /wishlist");
	return Results.Ok(result);
});

// ---------- 16) Выбрать (обновить) адрес доставки (PATCH /api/choose_address) ----------
app.MapMethods("/api/choose_address", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string newAddress) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(newAddress))
		return Results.BadRequest(new { error = "Адрес доставки не может быть пустым" });

	currentUser.ShippingAddress = newAddress;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /choose_address");
	return Results.Ok(new { message = "Адрес доставки обновлён" });
});

// ---------- 17) Выбрать (обновить) способ оплаты (PATCH /api/choose_payment) ----------
app.MapMethods("/api/choose_payment", new[] { "PATCH" }, async (AppDbContext db, HttpRequest req, string paymentMethod) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(paymentMethod))
		return Results.BadRequest(new { error = "Способ оплаты не может быть пустым" });

	currentUser.PaymentMethod = paymentMethod;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "PATCH /choose_payment");
	return Results.Ok(new { message = "Способ оплаты обновлён" });
});

// ---------- 18) Оформить заказ (POST /api/order) ----------
app.MapPost("/api/order", async (AppDbContext db, HttpRequest req, string? comment) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	// Собираем товары из корзины
	var cartItems = await db.CartItems
		.Where(ci => ci.UserId == currentUser.Id)
		.ToListAsync();
	if (!cartItems.Any())
		return Results.BadRequest(new { error = "Корзина пуста" });

	decimal totalPrice = 0;
	var orderItems = new List<OrderItem>();

	foreach (var ci in cartItems)
	{
		var product = await db.Products.FindAsync(ci.ProductId);
		if (product == null) continue;
		decimal itemPrice = product.Price * ci.Quantity;
		totalPrice += itemPrice;

		orderItems.Add(new OrderItem
		{
			ProductId = product.Id,
			Quantity = ci.Quantity,
			PriceAtPurchase = product.Price
		});
	}

	if (currentUser.Balance < totalPrice)
	{
		return Results.BadRequest(new { error = "Недостаточно средств на балансе" });
	}
	currentUser.Balance -= totalPrice;

	// Создаем заказ, добавляем поле Comment
	var order = new Order
	{
		UserId = currentUser.Id,
		CreatedAt = DateTime.UtcNow,
		TotalPrice = totalPrice,
		Comment = comment // <-- сохраняем комментарий
	};
	db.Orders.Add(order);
	await db.SaveChangesAsync();

	// Добавляем OrderItem в базу, с привязкой к order.Id
	foreach (var oi in orderItems)
	{
		oi.OrderId = order.Id;
	}
	db.OrderItems.AddRange(orderItems);

	// Очищаем корзину
	db.CartItems.RemoveRange(cartItems);

	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /order");
	return Results.Ok(new
	{
		message = "Заказ успешно создан",
		orderId = order.Id,
		totalPrice = order.TotalPrice,
		orderComment = order.Comment // можем вернуть обратно, если нужно
	});
});


// ---------- 19) Просмотр истории заказов (GET /api/orders/history) ----------
app.MapGet("/api/orders/history", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	// Ищем все заказы пользователя
	var userOrders = await db.Orders
		.Where(o => o.UserId == currentUser.Id)
		.OrderByDescending(o => o.CreatedAt)
		.ToListAsync();

	var result = new List<object>();
	foreach (var order in userOrders)
	{
		// Собираем OrderItems
		var items = await db.OrderItems
			.Where(oi => oi.OrderId == order.Id)
			.ToListAsync();

		var orderInfo = new
		{
			OrderId = order.Id,
			order.CreatedAt,
			order.TotalPrice,
			Comment = order.Comment,


			Items = items.Select(i => new
			{
				ProductId = i.ProductId,
				Quantity = i.Quantity,
				PriceAtPurchase = i.PriceAtPurchase
			})
		};

		result.Add(orderInfo);
	}

	SaveRequestHistory(db, currentUser.Id, "GET /orders/history");
	return Results.Ok(result);
});


// ---------- 20) История запросов (GET /api/requests_history) ----------
app.MapGet("/api/requests_history", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var history = await db.RequestHistories
		.Where(r => r.UserId == currentUser.Id)
		.OrderByDescending(r => r.Timestamp)
		.ToListAsync();

	SaveRequestHistory(db, currentUser.Id, "GET /requests_history");
	return Results.Ok(history.Select(h => new
	{
		h.Id,
		h.Endpoint,
		h.Timestamp
	}));
});

// ---------- 21) Удалить историю запросов (DELETE /api/requests_history) ----------
app.MapDelete("/api/requests_history", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	var userHistories = db.RequestHistories.Where(r => r.UserId == currentUser.Id);
	db.RequestHistories.RemoveRange(userHistories);
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "DELETE /requests_history");
	return Results.Ok(new { message = "История запросов очищена" });
});

// Добавить новый товар (POST /api/products/add) 
app.MapPost("/api/products/add", async (AppDbContext db, HttpRequest req, string name, decimal price, string? imageUrl = null) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null)
		return Results.Unauthorized();

	// Здесь можно проверить, что user - администратор, если требуется

	if (string.IsNullOrWhiteSpace(name))
		return Results.BadRequest(new { error = "Название товара не может быть пустым" });
	if (price <= 0)
		return Results.BadRequest(new { error = "Цена должна быть > 0" });

	var newProduct = new Product
	{
		Name = name,
		Price = price,
		ImageUrl = imageUrl // Сохраняем путь/URL к картинке, если передан
	};
	db.Products.Add(newProduct);
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /products/add");
	return Results.Ok(new
	{
		message = $"Товар '{name}' добавлен. ID = {newProduct.Id}, ImageUrl = {imageUrl}"
	});
});

// Посмотреть баланс (GET /api/balance)
app.MapGet("/api/balance", async (AppDbContext db, HttpRequest req) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null)
		return Results.Unauthorized();

	return Results.Ok(new
	{
		message = $"Ваш текущий баланс: {currentUser.Balance}",
		balance = currentUser.Balance
	});
});

// Пополнить баланс (POST /api/balance/add)
app.MapPost("/api/balance/add", async (AppDbContext db, HttpRequest req, decimal amount) =>
{
	var currentUser = await GetCurrentUser(db, req);
	if (currentUser == null) return Results.Unauthorized();

	if (amount <= 0)
		return Results.BadRequest(new { error = "Сумма должна быть больше 0" });

	currentUser.Balance += amount;
	await db.SaveChangesAsync();

	SaveRequestHistory(db, currentUser.Id, "POST /balance/add");
	return Results.Ok(new
	{
		message = $"Баланс пополнен. Текущий баланс: {currentUser.Balance}"
	});
});

// Запуск приложения
app.Run();

//
// ============================
//      МОДЕЛИ и КОНТЕКСТ БД
// ============================
public class AppDbContext : DbContext
{
	public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

	public DbSet<User> Users => Set<User>();
	public DbSet<RequestHistory> RequestHistories => Set<RequestHistory>();
	public DbSet<Product> Products => Set<Product>();

	public DbSet<CartItem> CartItems => Set<CartItem>();
	public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();

	public DbSet<Order> Orders => Set<Order>();
	public DbSet<OrderItem> OrderItems => Set<OrderItem>();
}

public class User
{
	public int Id { get; set; }

	public string Username { get; set; } = null!;
	public string Email { get; set; } = null!;
	public string PasswordHash { get; set; } = null!;
	public string Token { get; set; } = null!;

	public string? AvatarUrl { get; set; }
	public string? ShippingAddress { get; set; }
	public string? PaymentMethod { get; set; }

	public decimal Balance { get; set; } = 0;
}

public class RequestHistory
{
	public int Id { get; set; }
	public int UserId { get; set; }
	public string Endpoint { get; set; } = null!;
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class Product
{
	public int Id { get; set; }
	public string Name { get; set; } = null!;
	public decimal Price { get; set; }

	public string? ImageUrl { get; set; }
}

public class CartItem
{
	public int Id { get; set; }
	public int UserId { get; set; }
	public int ProductId { get; set; }
	public int Quantity { get; set; } = 1;
}

public class WishlistItem
{
	public int Id { get; set; }
	public int UserId { get; set; }
	public int ProductId { get; set; }
}

public class Order
{
	public int Id { get; set; }
	public int UserId { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public decimal TotalPrice { get; set; }

	public string? Comment { get; set; }
}


public class OrderItem
{
	public int Id { get; set; }
	public int OrderId { get; set; }
	public int ProductId { get; set; }
	public decimal PriceAtPurchase { get; set; }
	public int Quantity { get; set; }
}

static class AuthUtils
{
	public static string GenerateToken() => Guid.NewGuid().ToString("N");
}
