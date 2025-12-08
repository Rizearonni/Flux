Place Lua library files (.lua) or folders containing libraries here.

- The loader will automatically scan this `libs/` folder in addition to the legacy `Ace3/` workspace path.
- For best results drop Ace3 files or other library `.lua` files in this directory, keeping their relative paths where appropriate.
- Only a whitelist of common libs is auto-loaded by default (AceAddon-3.0, AceEvent-3.0, AceComm-3.0, AceConsole-3.0, AceDB-3.0, AceGUI-3.0, AceLocale-3.0, CallbackHandler-1.0, LibStub, LibDataBroker-1.1, LibDBIcon-1.0, AceTimer-3.0, AceConfig-3.0). You can add other libs to the folder and the program will load them if they appear in the whitelist or if you adjust the code.

To try:
1. Move or copy your `Ace3` folder contents (or specific `.lua` library files) into `libs/`.
2. Rebuild and run the app; logs will indicate which libs were loaded.

If you'd like, I can also update the whitelist or make the loader configurable via an app setting or environment variable.