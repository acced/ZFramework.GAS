using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 播放一段动画的驱动。多种动画形式（Animator / 旧版 Animation / 序列帧 / Spine…）
    /// 经这一个接口接进同一条时间轴，<see cref="AnimationTask"/> 不关心底层是什么。
    ///
    /// <b>时长是下达的，不是查询的</b>：<see cref="Play"/> 把动画拉伸到给定秒数再播。
    /// 于是引擎自己推进播放（保留过渡与混合），时间轴在 gameplay 时钟上推进，
    /// 两者按同一个时长走，不会漂移——不需要任何进度回查接口。
    /// </summary>
    public interface IAnimationDriver
    {
        /// <summary>
        /// 播放 <paramref name="clipName"/>，并把它拉伸到 <paramref name="duration"/> 秒。
        /// <paramref name="duration"/> 小于等于 0 表示用动画的自然时长。
        /// </summary>
        /// <returns>实际时长（秒）。找不到该动画时返回 0。</returns>
        float Play(string clipName, float duration, bool loop);

        /// <summary>
        /// 把本圈的归一化进度（0~1）交给驱动。
        /// 由引擎自行推进的实现（Animator / 旧版 Animation）无事可做。
        /// </summary>
        void Advance(float normalizedTime);

        /// <summary>停止播放。技能结束或被打断时调用。</summary>
        void Stop();
    }

    /// <summary>
    /// 旧版 <see cref="Animation"/> 组件的驱动——宿主战斗层今天用的就是它。
    ///
    /// 变速即「把 length 秒的片段播成 duration 秒」，正是战斗单位原本手写在攻击分支里的那一行；
    /// 这里把它变成所有动画形式共用的契约。
    /// </summary>
    public sealed class LegacyAnimationDriver : IAnimationDriver
    {
        private readonly Animation _animation;
        private string _playing;
        private float _restoreSpeed, _restoreTime;
        private WrapMode _restoreWrap;

        public LegacyAnimationDriver(Animation animation)
            => _animation = animation != null ? animation : throw new ArgumentNullException(nameof(animation));

        public float Play(string clipName, float duration, bool loop)
        {
            AnimationState state = _animation[clipName];
            if (state == null)
            {
                return 0f;
            }

            if (_playing != clipName)
            {
                Stop();
                _restoreSpeed = state.speed;
                _restoreTime = state.time;
                _restoreWrap = state.wrapMode;
            }

            // 先停再配再播：Animation.Play 对已在播放的片段不会回到起点，
            // 连续两次施法就会从上一次的中途接着放。
            _animation.Stop(clipName);

            bool stretch = duration > 0f && state.length > 0f;
            state.speed = stretch ? state.length / duration : 1f;
            state.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            state.time = 0f;
            _animation.Play(clipName);
            _playing = clipName;

            return stretch ? duration : state.length;
        }

        /// <summary>旧版 Animation 由引擎自行推进，无需外部喂进度。</summary>
        public void Advance(float normalizedTime)
        {
        }

        public void Stop()
        {
            if (_playing == null)
            {
                return;
            }

            // 组件可能已随单位一起销毁（回收、场景卸载），这不是错误。
            if (_animation != null)
            {
                _animation.Stop(_playing);
                var state = _animation[_playing];
                if (state != null) { state.speed = _restoreSpeed; state.time = _restoreTime; state.wrapMode = _restoreWrap; }
            }

            _playing = null;
        }
    }
}
