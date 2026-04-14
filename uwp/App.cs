using OpenRA;
using SDL2;
using System;
using System.Diagnostics.CodeAnalysis;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace openra_uwp
{
	class Wrapper
	{
		public static void Main(string[] args)
		{
			// Must run SDL apps via this call on UWP or init will fail
			SDL.SDL_WinRTRunApp(SDLMain, IntPtr.Zero);
		}

		private static int SDLMain(int count, IntPtr args)
		{
			string[] custom_args = { /*"Engine.EngineDir=E:/cnc",*/ "Game.Mod=ra" };

			// Inspired by OpenRA.Launcher
			try
			{
				Game.InitializeAndRun(custom_args);
			}
			catch
			{
				Log.Dispose();
				throw;
			}
			finally
			{
				Log.Dispose();
			}

			return 0;
		}
	}
}
