using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// <see cref="Animator"/> 的驱动：<paramref name="clipName"/> 即状态机里的状态名。
    ///
    /// 拉伸靠 <see cref="Animator.speed"/>，作用于整个 Animator——技能动画期间
    /// 该 Animator 应当由技能独占。<see cref="Stop"/> 恢复播放前的速度，而不是强设成 1：
    /// 速度可能本来就被别处调过（减速 debuff 等），技能不该顺手把它抹掉。
    ///
    /// <b>循环由状态自身的 clip 决定</b>，运行期改不了，因此 <c>loop</c> 参数在此实现中不生效；
    /// 时间轴侧的循环语义（标记点每圈重触发）仍由 <see cref="AnimationTask"/> 保证。
    /// </summary>
    public sealed class AnimatorAnimationDriver : IAnimationDriver
    {
        private readonly Animator _animator;
        private readonly int _layer;

        private float _restoreSpeed;
        private bool _playing;

        public AnimatorAnimationDriver(Animator animator, int layer = 0)
        {
            _animator = animator != null ? animator : throw new ArgumentNullException(nameof(animator));
            _layer = layer;
        }

        public float Play(string clipName, float duration, bool loop)
        {
            if (!_animator.HasState(_layer, Animator.StringToHash(clipName)))
            {
                // 一次都没播过就不许留下痕迹：Stop 据此判断该不该碰 speed。
                return 0f;
            }

            if (!_playing)
            {
                _restoreSpeed = _animator.speed;
                _playing = true;
            }

            _animator.speed = 1f;
            _animator.Play(clipName, _layer, 0f);

            // 状态切换要等 Animator 下次求值才生效，不推一下就只能读到上一个状态的时长。
            _animator.Update(0f);
            float natural = _animator.GetCurrentAnimatorStateInfo(_layer).length;

            if (duration > 0f && natural > 0f)
            {
                _animator.speed = natural / duration;
                return duration;
            }

            return natural;
        }

        /// <summary>Animator 由引擎自行推进，无需外部喂进度。</summary>
        public void Advance(float normalizedTime)
        {
        }

        /// <summary>
        /// 恢复播放前的速度。没播过就什么都不做——找不到状态时 <see cref="Play"/> 直接返回了 0，
        /// 此时若强设 speed = 1，一次拼错的片段名就会静默清掉别处施加的减速。
        /// </summary>
        public void Stop()
        {
            if (!_playing)
            {
                return;
            }

            // 组件可能已随单位一起销毁（回收、场景卸载），这不是错误。
            if (_animator != null)
            {
                _animator.speed = _restoreSpeed;
            }

            _playing = false;
        }
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
