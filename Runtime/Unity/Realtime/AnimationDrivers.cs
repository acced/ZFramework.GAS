using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// <see cref="Animator"/> 的驱动：<paramref name="clipName"/> 即状态机里的状态名。
    ///
    /// 拉伸靠 <see cref="Animator.speed"/>，作用于整个 Animator——技能动画期间
    /// 该 Animator 应当由技能独占。<see cref="Stop()"/> 恢复播放前的速度，而不是强设成 1：
    /// 速度可能本来就被别处调过（减速 debuff 等），技能不该顺手把它抹掉。
    ///
    /// 混入用 <see cref="Animator.CrossFadeInFixedTime(int, float, int, float)"/>：过渡时间同样受
    /// <see cref="Animator.speed"/> 缩放，因而与拉伸后的动作同比变化。混出时回到施放前所在的状态（通常是待机）。
    ///
    /// <b>循环由状态自身的 clip 决定</b>，运行期改不了，因此 <c>loop</c> 参数在此实现中不生效；
    /// 时间轴侧的循环语义（标记点每圈重触发）仍由 <see cref="AnimationTask"/> 保证。
    /// </summary>
    public sealed class AnimatorAnimationDriver : ISkillAnimationSpeed, IAdvancedBlendingDriver, ISeekableAnimationDriver, IAnimationDriverLifetime
    {
        private readonly Animator _animator;
        public bool IsValid => _animator != null;
        private readonly int _layer;

        private float _restoreSpeed, _natural, _speed=1;
        public void SetLocalScale(float scale){ if(IsValid && _playing) _animator.speed=_speed*scale; }
        public AnimationBlendCapabilities Capabilities => AnimationBlendCapabilities.SeekBlend;
        public float Play(string clip, float duration, bool loop, in SkillBlend blend) => Play(clip, duration, loop, blend.duration);
        public void Stop(in SkillBlend blend) => Stop(blend.duration);
        public bool SeekBlend(float normalizedTime, in SkillBlend blend)
        {
            if (!IsValid || !_playing || _natural <= 0 || _animator.speed <= 0) return false;
            if(_animator.IsInTransition(_layer) && Is(_animator.GetNextAnimatorStateInfo(_layer),_playingState))
            {
                // Mecanim can swallow a second CrossFade to its pending destination. Prefer the requested time.
                Seek(normalizedTime);return true;
            }
            _animator.CrossFadeInFixedTime(_playingState, blend.duration * _animator.speed, _layer, normalizedTime * _natural);
            return true;
        }
        private bool _playing;
        private int _playingState, _returnState;

        public AnimatorAnimationDriver(Animator animator, int layer = 0)
        {
            _animator = animator != null ? animator : throw new ArgumentNullException(nameof(animator));
            _layer = layer;
        }

        public float Play(string clipName, float duration, bool loop) => Play(clipName, duration, loop, 0f);

        public float Play(string clipName, float duration, bool loop, float blendIn)
        {
            return PlayAt(clipName, duration, loop, blendIn, 0);
        }

        internal float PlayAt(string clipName, float duration, bool loop, float blendIn, float entry)
        {
            int state = Animator.StringToHash(clipName);
            if (!_animator.HasState(_layer, state))
            {
                // 一次都没播过就不许留下痕迹：Stop 据此判断该不该碰 speed。
                return 0f;
            }

            if (!_playing)
            {
                _restoreSpeed = _animator.speed;
                // 连续施放时保留最早的返回点：连招结束后回到待机，而不是回到上一个技能。
                _returnState = CurrentOrNext().fullPathHash;
                _playing = true;
            }

            _animator.speed = 1f;
            // 目标就是当前状态时（同一技能再次施放）交叉淡入不会从头开始，直接重播。
            bool restart = Is(CurrentOrNext(), state);
            if (blendIn > 0f && !restart)
            {
                if (entry == 0) _animator.CrossFadeInFixedTime(state, blendIn, _layer, 0f);
                else
                {
                    // Normalized offset belongs to the destination; transition duration is relative to the source.
                    float sourceLength = _animator.GetCurrentAnimatorStateInfo(_layer).length;
                    _animator.CrossFade(state, blendIn / Mathf.Max(.0001f, sourceLength), _layer, entry);
                }
            }
            else _animator.Play(state, _layer, entry);
            _playingState = state;

            // 状态切换要等 Animator 下次求值才生效，不推一下就只能读到上一个状态的时长。
            _animator.Update(0f);
            float natural = (_animator.IsInTransition(_layer) ? _animator.GetNextAnimatorStateInfo(_layer) : _animator.GetCurrentAnimatorStateInfo(_layer)).length;

            _natural = natural; _speed=1;
            if (duration > 0f && natural > 0f)
            {
                _animator.speed = _speed = natural / duration;
                return duration;
            }

            return natural;
        }

        /// <summary>Animator 由引擎自行推进，无需外部喂进度。</summary>
        public void Advance(float normalizedTime)
        {
        }
        public void Seek(float normalizedTime)
        { if (_animator != null && _playing) { _animator.Play(_playingState, _layer, normalizedTime); _animator.Update(0); } }

        public void Stop() => Stop(0f);

        /// <summary>
        /// 恢复播放前的速度，并在 <paramref name="blendOut"/> 大于 0 时过渡回施放前的状态。
        /// 没播过就什么都不做——找不到状态时 <see cref="Play(string, float, bool, float)"/> 直接返回了 0，
        /// 此时若强设 speed = 1，一次拼错的片段名就会静默清掉别处施加的减速。
        /// 状态机自己的转换已经离开技能状态时，不再插手。
        /// </summary>
        public void Stop(float blendOut)
        {
            if (!_playing)
            {
                return;
            }

            _playing = false;

            // 组件可能已随单位一起销毁（回收、场景卸载），这不是错误。
            if (_animator == null)
            {
                return;
            }

            _animator.speed = _restoreSpeed;
            var settled = CurrentOrNext();
            if (blendOut > 0f && _returnState != 0 && _returnState != settled.fullPathHash && Is(settled, _playingState))
            {
                _animator.CrossFadeInFixedTime(_returnState, blendOut, _layer);
            }
        }

        /// <summary>正在过渡时以目标状态为准：那才是马上要停留的状态。</summary>
        private AnimatorStateInfo CurrentOrNext()
            => _animator.IsInTransition(_layer) ? _animator.GetNextAnimatorStateInfo(_layer) : _animator.GetCurrentAnimatorStateInfo(_layer);

        /// <summary>片段名可以写完整路径（Base Layer.Attack）也可以只写状态名，两种哈希都要认。</summary>
        private static bool Is(in AnimatorStateInfo info, int state) => info.fullPathHash == state || info.shortNameHash == state;
    }

    /// <summary>
    /// 序列帧驱动：按归一化进度换 <see cref="SpriteRenderer.sprite"/>。
    ///
    /// 这是三个适配器里唯一真正用到 <see cref="Advance"/> 的——它没有引擎侧的播放器，
    /// 完全靠时间轴推进，因此天然与判定同步。
    ///
    /// 一个实例只承载一段序列；<paramref name="clipName"/> 对不上时返回 0，
    /// 与「找不到片段」同义，不会静默播成另一段。
    ///
    /// <c>loop</c> 参数在此实现中不生效：<see cref="Advance"/> 收到的是<b>本圈</b>的归一化进度，
    /// 循环由 <see cref="AnimationTask"/> 每圈归零来表达，驱动这边看不出区别。
    /// </summary>
    public sealed class SpriteSequenceDriver : IAnimationDriver
    {
        private readonly SpriteRenderer _renderer;
        private readonly string _clipName;
        private readonly Sprite[] _frames;
        private readonly float _frameRate;

        private Sprite _restore;
        private bool _playing;

        public SpriteSequenceDriver(SpriteRenderer renderer, string clipName, Sprite[] frames, float frameRate)
        {
            _renderer = renderer != null ? renderer : throw new ArgumentNullException(nameof(renderer));

            if (string.IsNullOrEmpty(clipName))
            {
                throw new ArgumentException("Clip name must not be empty.", nameof(clipName));
            }

            if (frames == null || frames.Length == 0)
            {
                throw new ArgumentException("A sprite sequence needs at least one frame.", nameof(frames));
            }

            if (!(frameRate > 0f))
            {
                throw new ArgumentOutOfRangeException(nameof(frameRate), frameRate, "Frame rate must be positive.");
            }

            _clipName = clipName;
            _frames = frames;
            _frameRate = frameRate;
        }

        public float Play(string clipName, float duration, bool loop)
        {
            if (!string.Equals(clipName, _clipName, StringComparison.Ordinal))
            {
                return 0f;
            }

            if (!_playing)
            {
                _restore = _renderer.sprite;
                _playing = true;
            }

            _renderer.sprite = _frames[0];
            return duration > 0f ? duration : _frames.Length / _frameRate;
        }

        public void Advance(float normalizedTime)
        {
            if (!_playing || _renderer == null)
            {
                return;
            }

            int frame = (int)(normalizedTime * _frames.Length);
            if (frame < 0)
            {
                frame = 0;
            }
            else if (frame >= _frames.Length)
            {
                // 进度为 1 时正好落在末帧之后，收敛到最后一帧。
                frame = _frames.Length - 1;
            }

            _renderer.sprite = _frames[frame];
        }

        /// <summary>
        /// 恢复播放前的图。没有这一步，技能被打断时角色会永远定格在挥砍到一半的那一帧。
        /// </summary>
        public void Stop()
        {
            if (!_playing)
            {
                return;
            }

            if (_renderer != null)
            {
                _renderer.sprite = _restore;
            }

            _restore = null;
            _playing = false;
        }
    }
}
