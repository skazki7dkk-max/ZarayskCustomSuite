# Полная установка Zaraysk Menu + Warcraft + WeaponPaints 0.3.1

Архив содержит весь собранный комплект:

- ZarayskMenu и общую ZarayskMenu.Api;
- Warcraft `4.2.0-zaraysk-0.3.1` со всеми зависимостями и совместимой Linux SQLite;
- ZarayskWarcraftMenu `0.3.1`, `!war`, `!helpwar` и `css_warxp`;
- WeaponPaints `4.0.1 Build 95`, подключённый к ZarayskMenu;
- клиентские cfg и исходные файлы Panorama.

## Установка на сервер

1. Полностью остановите сервер и дождитесь статуса Offline.
2. Сохраните копию `WarcraftPlugin/database.db` и собственных конфигов.
3. Удалите старый файл `plugins/WarcraftPlugin/ZarayskWarcraftFixes.dll`, если он существует.
4. Скопируйте папку `game` из архива в корневую папку сервера с заменой файлов.
5. Запустите сервер без hot reload.
6. Выполните `css_plugins list`.

Ожидаются четыре загруженных плагина:

- `WeaponPaints 4.0.1 (Build 95)`;
- `Warcraft 4.2.0-zaraysk-0.3.1`;
- `ZarayskWarcraftMenu 0.3.1`;
- `ZarayskMenu 0.2.1`.

## Основные команды

- `!menu` — единое главное меню;
- `!war` — Warcraft;
- `!helpwar` — инструкция по всем классам;
- `!class`, `!skills`, `!shop` — разделы Warcraft;
- `css_warxp <цель> <количество>` — выдать опыт из серверной консоли;
- `bind x ultimate` — привязать ульту на X в клиентской консоли.

Папки `client`, `panorama` и `config` в архиве приложены как исходные файлы
интерфейса и примеры. Для обычной серверной установки достаточно папки `game`.
