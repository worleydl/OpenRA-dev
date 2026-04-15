#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Runtime.InteropServices;
using System.Text;
using SDL2;

using static SDL2.SDL.SDL_GameControllerAxis;
using static SDL2.SDL.SDL_GameControllerButton;

namespace OpenRA.Platforms.Default
{
	sealed class Sdl2Input
	{
		// virtual mouse state tracking
		const int VIRTUAL_DEADZONE = 1250;
		const int VIRTUAL_MOUSE_MAX_SPEED = 33;

		static int vm_dx = 0; // deltas
		static int vm_dy = 0;
		static int vm_px = 0; // position
		static int vm_py = 0;

		static float vmLastAxisX = 0;
		static float vmLastAxisY = 0;

		MouseButton lastButtonBits = MouseButton.None;

		IntPtr active_gamepad = IntPtr.Zero;

		public static string GetClipboardText() { return SDL.SDL_GetClipboardText(); }
		public static bool SetClipboardText(string text) { return SDL.SDL_SetClipboardText(text) == 0; }

		static MouseButton MakeButton(byte b)
		{
			return b == SDL.SDL_BUTTON_LEFT ? MouseButton.Left
				: b == SDL.SDL_BUTTON_RIGHT ? MouseButton.Right
				: b == SDL.SDL_BUTTON_MIDDLE ? MouseButton.Middle
				: 0;
		}

		static MouseButton MakeButtonFromGamepad(byte b)
		{
			return b == (byte)SDL_CONTROLLER_BUTTON_A ? MouseButton.Left
				: b == (byte)SDL_CONTROLLER_BUTTON_B ? MouseButton.Right
				: 0;
		}

		static Modifiers MakeModifiers(int raw)
		{
			return ((raw & (int)SDL.SDL_Keymod.KMOD_ALT) != 0 ? Modifiers.Alt : 0)
				 | ((raw & (int)SDL.SDL_Keymod.KMOD_CTRL) != 0 ? Modifiers.Ctrl : 0)
				 | ((raw & (int)SDL.SDL_Keymod.KMOD_LGUI) != 0 ? Modifiers.Meta : 0)
				 | ((raw & (int)SDL.SDL_Keymod.KMOD_RGUI) != 0 ? Modifiers.Meta : 0)
				 | ((raw & (int)SDL.SDL_Keymod.KMOD_SHIFT) != 0 ? Modifiers.Shift : 0);
		}

		static int2 EventPosition(Sdl2PlatformWindow device, int x, int y)
		{
			// On Windows and Linux (X11) events are given in surface coordinates
			// These must be scaled to our effective window coordinates
			// Round fractional components up to avoid rounding small deltas to 0
			if (Platform.CurrentPlatform != PlatformType.OSX && device.EffectiveWindowSize != device.SurfaceSize)
			{
				var s = 1 / device.EffectiveWindowScale;
				return new int2((int)(Math.Sign(x) / 2f + x * s), (int)(Math.Sign(x) / 2f + y * s));
			}

			// On macOS we must still account for the user-requested scale modifier
			if (Platform.CurrentPlatform == PlatformType.OSX && device.EffectiveWindowScale != device.NativeWindowScale)
			{
				var s = device.NativeWindowScale / device.EffectiveWindowScale;
				return new int2((int)(Math.Sign(x) / 2f + x * s), (int)(Math.Sign(x) / 2f + y * s));
			}

			return new int2(x, y);
		}

		private void SetupGamepad()
		{
			if (active_gamepad != IntPtr.Zero)
			{
				SDL.SDL_GameControllerClose(active_gamepad);
			}

			for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
			{
				if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
				{
					active_gamepad = SDL.SDL_GameControllerOpen(i);
					break;
				}
			}
		}

		public void PumpInput(Sdl2PlatformWindow device, IInputHandler inputHandler, int2? lockedMousePosition)
		{
			var mods = MakeModifiers((int)SDL.SDL_GetModState());
			inputHandler.ModifierKeys(mods);
			MouseInput? pendingMotion = null;

			// Apply virtual mouse state if deltas are lit
			if (vm_dx != 0 || vm_dy != 0)
			{
				vm_px += vm_dx;
				vm_py += vm_dy;

				vm_px = Math.Clamp(vm_px, 0, device.EffectiveWindowSize.Width);
				vm_py = Math.Clamp(vm_py, 0, device.EffectiveWindowSize.Height);

				var mousePos = new int2(vm_px, vm_py);
				var input = lockedMousePosition ?? mousePos;
				var pos = new int2(input.X, input.Y);
				// todo: may need scaling back if the ui elements don't register right at 200%
				//EventPosition(device, input.X, input.Y);

				var delta = lockedMousePosition == null
							? new int2(vm_dx, vm_dy) //EventPosition(device, vm_dx, vm_dy)
							: mousePos - lockedMousePosition.Value;

				pendingMotion = new MouseInput(
							MouseInputEvent.Move, lastButtonBits, pos, delta, mods, 0);
			}

			while (SDL.SDL_PollEvent(out var e) != 0)
			{
				switch (e.type)
				{
					case SDL.SDL_EventType.SDL_QUIT:
						// On macOS, we'd like to restrict Cmd + Q from suddenly exiting the game.
						if (Platform.CurrentPlatform != PlatformType.OSX || !mods.HasModifier(Modifiers.Meta))
							Game.Exit();

						break;

					case SDL.SDL_EventType.SDL_WINDOWEVENT:
					{
						switch (e.window.windowEvent)
						{
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
								device.HasInputFocus = false;
								break;

							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
								device.HasInputFocus = true;
								break;

							// Triggered when moving between displays with different DPI settings
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED:
								device.WindowSizeChanged();
								break;

							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_HIDDEN:
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MINIMIZED:
								device.IsSuspended = true;
								break;

							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_EXPOSED:
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_SHOWN:
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MAXIMIZED:
							case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_RESTORED:
								device.IsSuspended = false;
								break;
						}

						break;
					}

					case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
					case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
						SetupGamepad();

						break;

					case SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION:
					{
						if (e.caxis.axis == (byte) SDL_CONTROLLER_AXIS_LEFTX
							|| e.caxis.axis == (byte) SDL_CONTROLLER_AXIS_LEFTY)
						{
							float rawX = (e.caxis.axis == (byte)SDL_CONTROLLER_AXIS_LEFTX) ? e.caxis.axisValue : vmLastAxisX;
							float rawY = (e.caxis.axis == (byte)SDL_CONTROLLER_AXIS_LEFTY) ? e.caxis.axisValue : vmLastAxisY;

							vmLastAxisX = rawX;
							vmLastAxisY = rawY;

							float magnitude = (float)Math.Sqrt((double)rawX * rawX + (double)rawY * rawY);

							if (magnitude > VIRTUAL_DEADZONE)
							{
								float normalizedMag = Math.Min(1.0f, (magnitude - VIRTUAL_DEADZONE) / (32767 - VIRTUAL_DEADZONE));

								float curvedMag = (float)Math.Pow(normalizedMag, 3.0);

								float scale = (curvedMag * VIRTUAL_MOUSE_MAX_SPEED) / magnitude;

								vm_dx = (int)(rawX * scale);
								vm_dy = (int)(rawY * scale);
							}
							else
							{
								vm_dx = 0;
								vm_dy = 0;
							}
						}

						break;
					}

					case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
					case SDL.SDL_EventType.SDL_CONTROLLERBUTTONUP:
					{
						if(e.cbutton.button == (byte) SDL_CONTROLLER_BUTTON_A
							|| e.cbutton.button == (byte) SDL_CONTROLLER_BUTTON_B)
						{
							if (pendingMotion != null)
							{
								inputHandler.OnMouseInput(pendingMotion.Value);
								pendingMotion = null;
							}

							var button = MakeButtonFromGamepad(e.cbutton.button);

							if (e.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN)
								lastButtonBits |= button;
							else
								lastButtonBits &= ~button;

							var pos = new int2(vm_px, vm_py); // EventPosition(device, vm_px, vm_py);

							if (e.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN)
								inputHandler.OnMouseInput(new MouseInput(
									MouseInputEvent.Down, button, pos, int2.Zero, mods,
									MultiTapDetection.DetectFromMouse(e.button.button, pos)));
							else
								inputHandler.OnMouseInput(new MouseInput(
									MouseInputEvent.Up, button, pos, int2.Zero, mods,
									MultiTapDetection.InfoFromMouse(e.button.button)));
						}


						break;
					}

					case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
					case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
					{
						// Mouse 1, Mouse 2 and Mouse 3 are handled as mouse inputs
						// Mouse 4 and Mouse 5 are treated as (pseudo) keyboard inputs
						if (e.button.button == SDL.SDL_BUTTON_LEFT ||
							e.button.button == SDL.SDL_BUTTON_MIDDLE ||
							e.button.button == SDL.SDL_BUTTON_RIGHT)
						{
							if (pendingMotion != null)
							{
								inputHandler.OnMouseInput(pendingMotion.Value);
								pendingMotion = null;
							}

							var button = MakeButton(e.button.button);

							if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN)
								lastButtonBits |= button;
							else
								lastButtonBits &= ~button;

							var input = lockedMousePosition ?? new int2(e.button.x, e.button.y);
							var pos = EventPosition(device, input.X, input.Y);

							if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN)
								inputHandler.OnMouseInput(new MouseInput(
									MouseInputEvent.Down, button, pos, int2.Zero, mods,
									MultiTapDetection.DetectFromMouse(e.button.button, pos)));
							else
								inputHandler.OnMouseInput(new MouseInput(
									MouseInputEvent.Up, button, pos, int2.Zero, mods,
									MultiTapDetection.InfoFromMouse(e.button.button)));
						}

						if (e.button.button == SDL.SDL_BUTTON_X1 ||
							e.button.button == SDL.SDL_BUTTON_X2)
						{
							Keycode keyCode;

							if (e.button.button == SDL.SDL_BUTTON_X1)
								keyCode = Keycode.MOUSE4;
							else
								keyCode = Keycode.MOUSE5;

							var type = e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN ?
								KeyInputEvent.Down : KeyInputEvent.Up;

							var tapCount = e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN ?
								MultiTapDetection.DetectFromKeyboard(keyCode, mods) :
								MultiTapDetection.InfoFromKeyboard(keyCode, mods);

							var keyEvent = new KeyInput
							{
								Event = type,
								Key = keyCode,
								Modifiers = mods,
								UnicodeChar = '?',
								MultiTapCount = tapCount,
								IsRepeat = e.key.repeat != 0
							};
							inputHandler.OnKeyInput(keyEvent);
						}

						break;
					}

					case SDL.SDL_EventType.SDL_MOUSEMOTION:
					{
						var mousePos = new int2(e.motion.x, e.motion.y);
						var input = lockedMousePosition ?? mousePos;
						var pos = EventPosition(device, input.X, input.Y);

						var delta = lockedMousePosition == null
							? EventPosition(device, e.motion.xrel, e.motion.yrel)
							: mousePos - lockedMousePosition.Value;

						pendingMotion = new MouseInput(
							MouseInputEvent.Move, lastButtonBits, pos, delta, mods, 0);

						break;
					}

					case SDL.SDL_EventType.SDL_MOUSEWHEEL:
					{
						SDL.SDL_GetMouseState(out var x, out var y);

						var pos = EventPosition(device, x, y);
						inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Scroll, MouseButton.None, pos, new int2(0, e.wheel.y), mods, 0));

						break;
					}

					case SDL.SDL_EventType.SDL_TEXTINPUT:
					{
						var rawBytes = new byte[SDL.SDL_TEXTINPUTEVENT_TEXT_SIZE];
						unsafe { Marshal.Copy((IntPtr)e.text.text, rawBytes, 0, SDL.SDL_TEXTINPUTEVENT_TEXT_SIZE); }
						inputHandler.OnTextInput(Encoding.UTF8.GetString(rawBytes, 0, rawBytes.IndexOf((byte)0)));
						break;
					}

					case SDL.SDL_EventType.SDL_KEYDOWN:
					case SDL.SDL_EventType.SDL_KEYUP:
					{
						var keyCode = (Keycode)e.key.keysym.sym;
						var type = e.type == SDL.SDL_EventType.SDL_KEYDOWN ?
							KeyInputEvent.Down : KeyInputEvent.Up;

						var tapCount = e.type == SDL.SDL_EventType.SDL_KEYDOWN ?
							MultiTapDetection.DetectFromKeyboard(keyCode, mods) :
							MultiTapDetection.InfoFromKeyboard(keyCode, mods);

						var keyEvent = new KeyInput
						{
							Event = type,
							Key = keyCode,
							Modifiers = mods,
							UnicodeChar = (char)e.key.keysym.sym,
							MultiTapCount = tapCount,
							IsRepeat = e.key.repeat != 0
						};

						// Special case workaround for windows users
						if (e.key.keysym.sym == SDL.SDL_Keycode.SDLK_F4 && mods.HasModifier(Modifiers.Alt) &&
							Platform.CurrentPlatform == PlatformType.Windows)
							Game.Exit();
						else
							inputHandler.OnKeyInput(keyEvent);

						break;
					}
				}
			}

			if (pendingMotion != null)
				inputHandler.OnMouseInput(pendingMotion.Value);
		}
	}
}
