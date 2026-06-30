# Sols-Weather

Standalone extraction sandbox for the Fishbox sky, time-of-day, calendar, and water systems.

Open `Assets/Scenes/SolsWeather_Demo.unity` in Unity `6000.3.9f1` to test the copied stack. This is intentionally not a Unity package yet; it is a clean project where the systems can be stabilized before packaging.

Fishbox gameplay integrations such as save/load, sleep, NPC schedules, fishing, inventory, and audio are not included. Water trigger and underwater state hooks are exposed as events so project-specific adapters can be added later.
