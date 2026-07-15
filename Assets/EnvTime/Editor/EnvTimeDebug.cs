// EnvTimeDebug.cs
// EnvTime 项目的统一日志封装，支持通过宏定义控制日志输出

//#define ENVTIME_ENABLE_DEBUG // 默认关闭
using UnityEngine;


namespace BYTools.EnvTimeline
{
    /// <summary>
    /// EnvTime 项目的统一日志调试类
    /// 
    /// 使用方法:
    /// 1. 在项目 Build Settings 中添加自定义宏定义 "ENVTIME_ENABLE_DEBUG" 来启用日志
    /// 2. 或在代码顶部使用 #define ENVTIME_ENABLE_DEBUG 局部启用
    /// 3. 未定义宏时，所有日志调用会被编译器优化移除（零性能开销）
    /// </summary>
    public static class EnvTimeDebug
    {
#if ENVTIME_ENABLE_DEBUG
        /// <summary>
        /// 启用/禁用日志输出的运行时开关（需 ENVTIME_ENABLE_DEBUG 宏已定义）
        /// </summary>
        public static bool EnableLog { get; set; } = true;

#else
        // 未定义宏时提供一个静态属性（永远不会被调用）
        public static bool EnableLog { get; set; } = false;
#endif

        /// <summary>
        /// 日志前缀
        /// </summary>
        private const string LOG_PREFIX = "[EnvTime]";
        /// <summary>
        /// 记录普通日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void Log(object message)
        {
            if (EnableLog)
                Debug.Log($"{LOG_PREFIX} {message}");
        }

        /// <summary>
        /// 记录普通日志（带颜色标签）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void Log(object message, UnityEngine.Object context)
        {
            if (EnableLog)
                Debug.Log($"{LOG_PREFIX} {message}", context);
        }

        /// <summary>
        /// 记录彩色日志（使用富文本颜色标签）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogColor(object message, string colorTag)
        {
            if (EnableLog)
                Debug.Log($"{LOG_PREFIX} <color={colorTag}>{message}</color>");
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogWarning(object message)
        {
            if (EnableLog)
                Debug.LogWarning($"{LOG_PREFIX} {message}");
        }

        /// <summary>
        /// 记录警告日志（带上下文对象）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogWarning(object message, UnityEngine.Object context)
        {
            if (EnableLog)
                Debug.LogWarning($"{LOG_PREFIX} {message}", context);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogError(object message)
        {
            if (EnableLog)
                Debug.LogError($"{LOG_PREFIX} {message}");
        }

        /// <summary>
        /// 记录错误日志（带上下文对象）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogError(object message, UnityEngine.Object context)
        {
            if (EnableLog)
                Debug.LogError($"{LOG_PREFIX} {message}", context);
        }

        /// <summary>
        /// 记录断言失败日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogAssertion(object message)
        {
            if (EnableLog)
                Debug.LogAssertion($"{LOG_PREFIX} {message}");
        }

        /// <summary>
        /// 记录断言失败日志（带上下文对象）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogAssertion(object message, UnityEngine.Object context)
        {
            if (EnableLog)
                Debug.LogAssertion($"{LOG_PREFIX} {message}", context);
        }

        /// <summary>
        /// 记录异常日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogException(System.Exception exception)
        {
            if (EnableLog)
                Debug.LogException(exception);
        }

        /// <summary>
        /// 记录异常日志（带上下文对象）
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogException(System.Exception exception, UnityEngine.Object context)
        {
            if (EnableLog)
                Debug.LogException(exception, context);
        }

        #region 格式化日志方法

        /// <summary>
        /// 记录格式化普通日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogFormat(string format, params object[] args)
        {
            if (EnableLog)
                Debug.LogFormat($"{LOG_PREFIX} {format}", args);
        }

        /// <summary>
        /// 记录格式化警告日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogWarningFormat(string format, params object[] args)
        {
            if (EnableLog)
                Debug.LogWarningFormat($"{LOG_PREFIX} {format}", args);
        }

        /// <summary>
        /// 记录格式化错误日志
        /// </summary>
        [System.Diagnostics.Conditional("ENVTIME_ENABLE_DEBUG")]
        public static void LogErrorFormat(string format, params object[] args)
        {
            if (EnableLog)
                Debug.LogErrorFormat($"{LOG_PREFIX} {format}", args);
        }

        #endregion
    }
}