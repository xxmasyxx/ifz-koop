using Controllers.Time;
using MapEssentials;
using UI.Clock;
using UnityEngine;
using Zenject;
using ZenjectExtensions.Installers;

namespace IfzCoop.Game
{
    /// <summary>
    /// Zugriff auf die Objekte des Spiels über dessen Zenject-Container.
    /// Der Spielszenen-Container ist der SceneContext, in dem TimeChangerController gebunden ist
    /// (Installers/GameInstaller.cs).
    /// </summary>
    internal static class GameAccess
    {
        private static SceneContext _gameScene;

        public static bool IsGameStarted => GameInitializer.IsGameStarted;

        public static DiContainer GameContainer
        {
            get
            {
                if (_gameScene != null && _gameScene.Container != null)
                    return _gameScene.Container;
                _gameScene = null;
                foreach (var ctx in Object.FindObjectsByType<SceneContext>(FindObjectsSortMode.None))
                {
                    if (ctx != null && ctx.Container != null && ctx.Container.HasBinding<TimeChangerController>())
                    {
                        _gameScene = ctx;
                        return ctx.Container;
                    }
                }
                return null;
            }
        }

        public static T Game<T>() where T : class
        {
            return GameContainer?.TryResolve<T>();
        }

        public static T Project<T>() where T : class
        {
            return ProjectContext.Instance?.Container?.TryResolve<T>();
        }

        public static TimeChangerController TimeChanger => Game<TimeChangerController>();
        public static TimeSpeedSetter SpeedSetter => Game<TimeSpeedSetter>();
        public static Map Map => Game<Map>();
        public static SaveValidator SaveValidator => Project<SaveValidator>();

        public static string GameVersion => Application.version;
    }
}
