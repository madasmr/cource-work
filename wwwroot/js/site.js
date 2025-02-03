// Пример wwwroot/js/site.js

document.addEventListener('DOMContentLoaded', async function () {
    var userMenu = document.getElementById('userMenu');
    var token = localStorage.getItem('token');

    // Если токен отсутствует – пользователь не авторизован
    if (!token) {
        userMenu.innerHTML = `
            <ul>
                <li><a href="/Login">Вход</a></li>
                <li><a href="/Register">Регистрация</a></li>
            </ul>
        `;
    } else {
        // Если токен есть – делаем запрос на получение баланса
        let balance = '0';
        try {
            const response = await fetch('/api/balance', {
                headers: { 'Authorization': 'Bearer ' + token }
            });
            if (response.ok) {
                const data = await response.json();
                balance = data.balance !== undefined ? data.balance : '0';
            }
        } catch (e) {
            console.error('Ошибка при получении баланса:', e);
        }
        userMenu.innerHTML = `
            <ul>
                <li><a href="/PersonalCabinet">Личный кабинет</a></li>
                <li><a href="/Wishlist">Желаемое</a></li>
                <li><a href="/Cart">Корзина</a></li>
                <li><span>Баланс: ${balance} руб.</span></li>
                <li><a href="#" onclick="logout(); return false;">Выход</a></li>
            </ul>
        `;
    }
});

function logout() {
    localStorage.removeItem('token');
    window.location.reload();
}

