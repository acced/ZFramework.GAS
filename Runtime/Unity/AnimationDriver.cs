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
    public interface ISeekableAnimationDriver : IAnimationDriver
    {
        void Seek(float normalizedTime);
    }
    public interface ISkillAnimationSpeed { void SetLocalScale(float scale); }
    public interface IAnimationDriverLifetime { bool IsValid { get; } }

    /// <summary>
    /// 能平滑过渡的驱动。<see cref="AnimationTask"/> 检测到驱动实现了本接口时，
    /// 按技能动画的混入、混出时长调用下面两个重载；否则退回 <see cref="IAnimationDriver"/> 的硬切。
    ///
    /// <b>混入随实际时长缩放</b>：它是动作的一部分，攻速拉快时与标记点同比变短。
    /// <b>混出按实际秒数</b>：它发生在技能动画之后，与攻速无关。
    /// </summary>
    public interface IBlendingAnimationDriver : IAnimationDriver
    {
        /// <param name="blendIn">从当前动画过渡进来的时长（时间轴秒数，驱动自行按拉伸比例换算）；0 为硬切。</param>
        float Play(string clipName, float duration, bool loop, float blendIn);

        /// <param name="blendOut">过渡回施放前动画的实际秒数；0 时驱动不切换状态，只做清理。</param>
        void Stop(float blendOut);
    }

    /// <summary>
    /// 旧版 <see cref="Animation"/> 组件的驱动——宿主战斗层今天用的就是它。
    ///
    /// 变速即「把 length 秒的片段播成 duration 秒」，正是战斗单位原本手写在攻击分支里的那一行；
    /// 这里把它变成所有动画形式共用的契约。混入、混出用 <see cref="Animation.CrossFade(string, float)"/>，
    /// 混出时淡回施放前权重最高的片段（通常是待机）。
    /// </summary>
    public sealed class LegacyAnimationDriver : ISkillAnimationSpeed, IBlendingAnimationDriver, ISeekableAnimationDriver, IAnimationDriverLifetime
    {
        private readonly Animation _animation;
        public bool IsValid => _animation != null;
        private string _playing, _return;
        private float _restoreSpeed, _restoreTime, _speed;
        public void SetLocalScale(float scale){ if(_animation!=null && _playing!=null && _animation[_playing] is AnimationState s) s.speed=_speed*scale; }
        private WrapMode _restoreWrap;

        public LegacyAnimationDriver(Animation animation)
            => _animation = animation != null ? animation : throw new ArgumentNullException(nameof(animation));

        public float Play(string clipName, float duration, bool loop) => Play(clipName, duration, loop, 0f);

        public float Play(string clipName, float duration, bool loop, float blendIn)
        {
            AnimationState state = _animation[clipName];
            if (state == null)
            {
                return 0f;
            }

            if (_playing == null)
            {
                _return = Dominant(clipName);
            }

            if (_playing != clipName)
            {
                // 连续施放时不硬停上一个技能，交叉淡入会把它淡出；只把它的设置还原。
                Restore(_playing, false);
                _restoreSpeed = state.speed;
                _restoreTime = state.time;
                _restoreWrap = state.wrapMode;
            }

            // 先停再配再播：Animation.Play 对已在播放的片段不会回到起点，
            // 连续两次施法就会从上一次的中途接着放。
            _animation.Stop(clipName);

            bool stretch = duration > 0f && state.length > 0f;
            state.speed = _speed = stretch ? state.length / duration : 1f;
            state.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
            state.time = 0f;
            // 淡入时长按实际秒数计算：时间轴上的混入随拉伸同比缩放。
            if (blendIn > 0f) _animation.CrossFade(clipName, blendIn / state.speed);
            else _animation.Play(clipName);
            _playing = clipName;

            return stretch ? duration : state.length;
        }

        /// <summary>旧版 Animation 由引擎自行推进，无需外部喂进度。</summary>
        public void Advance(float normalizedTime)
        {
        }
        public void Seek(float normalizedTime)
        { if (_animation != null && _playing != null && _animation[_playing] is AnimationState state) { state.time = normalizedTime * state.length; _animation.Sample(); } }

        public void Stop() => Stop(0f);

        public void Stop(float blendOut)
        {
            if (_playing == null)
            {
                return;
            }

            // 组件可能已随单位一起销毁（回收、场景卸载），这不是错误。
            if (_animation != null)
            {
                bool fade = blendOut > 0f && _return != null && _animation[_return] != null;
                if (fade) _animation.CrossFade(_return, blendOut);
                else _animation.Stop(_playing);
                // 淡出期间不回拨时间，否则正在淡出的姿势会跳一下。
                Restore(_playing, !fade);
            }

            _playing = null;
            _return = null;
        }

        private void Restore(string clipName, bool time)
        {
            var state = clipName != null ? _animation[clipName] : null;
            if (state == null) return;
            state.speed = _restoreSpeed;
            state.wrapMode = _restoreWrap;
            if (time) state.time = _restoreTime;
        }

        /// <summary>施放前权重最高的片段，混出时淡回它。</summary>
        private string Dominant(string exclude)
        {
            string best = null; float weight = 0f;
            foreach (AnimationState candidate in _animation)
            {
                if (candidate.enabled && candidate.weight > weight && candidate.name != exclude) { best = candidate.name; weight = candidate.weight; }
            }

            return best;
        }
    }
}
