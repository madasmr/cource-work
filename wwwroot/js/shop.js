// wwwroot/js/shop.js

// Функция для добавления товара в корзину
async function addToCart(productId, quantity) {
    const token = localStorage.getItem('token');
    if (!token) {
        alert("Пожалуйста, выполните вход для добавления товаров в корзину.");
        return;
    }
    try {
        const response = await fetch(`/api/cart/add?productId=${productId}&quantity=${quantity}`, {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token }
        });
        const data = await response.json();
        if (response.ok) {
            alert(data.message);
        } else {
            alert(data.error || 'Ошибка при добавлении в корзину');
        }
    } catch (e) {
        console.error(e);
        alert('Ошибка при отправке запроса.');
    }
}

// Функция для добавления товара в «Желаемое»
async function addToWishlist(productId) {
    const token = localStorage.getItem('token');
    if (!token) {
        alert("Пожалуйста, выполните вход для добавления товаров в желаемое.");
        return;
    }
    try {
        const response = await fetch(`/api/wishlist/add?productId=${productId}`, {
            method: 'POST',
            headers: { 'Authorization': 'Bearer ' + token }
        });
        const data = await response.json();
        if (response.ok) {
            alert(data.message);
        } else {
            alert(data.error || 'Ошибка при добавлении в желаемое');
        }
    } catch (e) {
        console.error(e);
        alert('Ошибка при отправке запроса.');
    }
}
