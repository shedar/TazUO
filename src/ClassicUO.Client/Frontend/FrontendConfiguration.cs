using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ClassicUO.Frontend;

internal enum FrontendMode
{
    Local,
    Null
}

internal sealed class FrontendOptions
{
    public static FrontendOptions Default { get; } = new();

    public FrontendMode Mode { get; init; } = FrontendMode.Local;
    public int FramesPerSecond { get; init; } = 15;
    public int WebSocketPort { get; init; } = 19870;
    public bool HideNativeWindow { get; init; } = true;
    public string InstrumentationPath { get; init; }
}

internal static class FrontendConfiguration
{
    public static FrontendOptions Current { get; private set; } = FrontendOptions.Default;

    public static void Initialize(IReadOnlyList<string> arguments)
    {
        Current = Parse(arguments);
    }

    internal static FrontendOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        FrontendMode mode = FrontendMode.Local;
        int framesPerSecond = 15;
        int webSocketPort = 19870;
        bool hideNativeWindow = true;
        string instrumentationPath = null;

        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];

            if (string.IsNullOrWhiteSpace(argument) || argument[0] != '-')
            {
                continue;
            }

            string option = argument[1..].Replace('_', '-').ToLowerInvariant();

            switch (option)
            {
                case "frontend":
                {
                    string value = RequireValue(arguments, ref i, option).ToLowerInvariant();
                    mode = value switch
                    {
                        "local" => FrontendMode.Local,
                        "null" or "none" => FrontendMode.Null,
                        _ => throw new ArgumentException(
                            $"Unsupported frontend mode '{value}'. Expected local or null."
                        )
                    };
                    break;
                }
                case "frontend-fps":
                {
                    string value = RequireValue(arguments, ref i, option);

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out framesPerSecond)
                        || framesPerSecond is < 1 or > 60)
                    {
                        throw new ArgumentException("frontend-fps must be an integer from 1 through 60.");
                    }

                    break;
                }
                case "frontend-port":
                {
                    string value = RequireValue(arguments, ref i, option);

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out webSocketPort)
                        || webSocketPort is < 1024 or > 65535)
                    {
                        throw new ArgumentException("frontend-port must be an integer from 1024 through 65535.");
                    }

                    break;
                }
                case "frontend-hide-window":
                {
                    string value = RequireValue(arguments, ref i, option);

                    if (!bool.TryParse(value, out hideNativeWindow))
                    {
                        throw new ArgumentException("frontend-hide-window must be true or false.");
                    }

                    break;
                }
                case "frontend-instrumentation":
                    instrumentationPath = Path.GetFullPath(RequireValue(arguments, ref i, option));
                    break;
            }
        }

        return new FrontendOptions
        {
            Mode = mode,
            FramesPerSecond = framesPerSecond,
            WebSocketPort = webSocketPort,
            HideNativeWindow = hideNativeWindow,
            InstrumentationPath = instrumentationPath
        };
    }

    private static string RequireValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (index + 1 >= arguments.Count
            || string.IsNullOrWhiteSpace(arguments[index + 1])
            || arguments[index + 1][0] == '-')
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return arguments[++index];
    }
}
