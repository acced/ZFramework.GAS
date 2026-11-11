# 美术方向与来源

## 本章视觉目标

黑石港是一座旧时代留下的海港城。界面以深蓝灰底、旧金属细边、象牙色衬线字和羊皮纸为主；当铺温暖，庭院有秋日暮光，地下路线保持古地图感。参考《圣兽之王》的 2D 手绘奇幻呈现质量，环境、城市、人物名称和故事均为本项目原创。

三张环境画通过内置 **imagegen** 生成并直接纳入本分支，尺寸为 1672×941。它们是实际游戏使用的背景，不是把 UI 烘焙进去的宣传图。精确格子、路线节点、文字、血条、角色原点和按钮由代码绘制。

| 文件 | 用途 |
| --- | --- |
| `Resources/IronOath/Art/BlackstoneCourtyard.png` | 战场庭院与编队背景 |
| `Resources/IronOath/Art/PawnshopInterior.png` | 当铺与双主角选择背景 |
| `Resources/IronOath/Art/BlackstoneMap.png` | 步进地下城地图底图 |
| `Resources/IronOath/Fonts/IronOathSerif.otf` | 基于 Noto Serif CJK SC 的重命名字形子集；OFL 许可证在相邻文件 |

没有生成 Spine 骨骼、角色切片或现成游戏角色。当前纹章式角色占位体由 `IronOathFigure` 绘制，便于检查站位和替换 Prefab。最终角色模型、动作、受击动画与声音需要项目资产接入。

## 生成提示词

### BlackstoneCourtyard

Use case: stylized-concept. Asset type: original premium 2D tactical RPG battle environment, game-ready background plate, wide landscape 16:9 1920x1080 or higher. Project: Iron Oath Pawnshop, a young brother and sister's medieval dark-fantasy border harbor adventure. Create a sumptuous hand-painted fantasy illustration with layered painterly texture, intricate architecture and foliage, warm bronze sunbeams against slate blue shadows, visual craftsmanship comparable to a premium Vanillaware-like 2D RPG but ORIGINAL setting and architecture. Scene: ruined Blackstone Harbor chapel courtyard above the ocean at late afternoon. Crooked weathered gothic arcade on right, broken rose window and hanging ivy, distant harbor citadel and sailing ships through mist left, cypress trees, delicate warm lanterns, scattered red leaves. Crucial gameplay composition: fixed camera slightly elevated side view. Broad perfectly continuous open horizontal stone courtyard floor spans bottom 55% of image, clear and unoccupied, suitable for two facing groups of 2D characters in left and right thirds. Ground reads as floor plane, subtle stone perspective, no dramatic tilting. Midground trees, walls and scenery should be above the primary actor area. Foreground only small corner flowers/moss. Rich luxurious painted details concentrated at upper frame, keep central floor calm and legible. Clear silhouette layering, cinematic atmosphere, exceptionally polished game art. No humans, no characters, no creatures, no UI, no grids, no labels, no words, no logos, no watermark, no Spine sprites, no baked character shadows.

### PawnshopInterior

Use case: stylized-concept. Asset type: polished original 2D RPG pawnshop interior background, wide landscape 16:9 1920x1080 or larger, game-ready painting without interface. World: Iron Oath Pawnshop in Blackstone Harbor, medieval fantasy frontier port. Primary request: an exceptionally beautiful hand-painted illustrated interior, premium 2D fantasy strategy RPG craftsmanship, elaborate but cozy. Rich walnut counter centered slightly low, brass merchant scales, a small open accounts ledger, antique sword leaning at rear, potion bottles, folded fabrics and a modest scarred iron cuirass, old parental adventurers' travel memorabilia. Tall diamond-paned window at left reveals misty seaside harbor roofs; late golden afternoon shafts illuminate dust motes. Behind the counter, exquisite wooden cabinets, rolled maps, small reliquary, candles, a soft blue cloak over chair. Moody navy teal shadows balanced by honey gold candlelight and muted burgundy. Painterly fine brushwork, convincing hand-built medieval materials, richly layered atmospheric depth, tasteful composition and beautiful high-quality original environment. Empty stage with no characters: reserved clear central space where UI can overlay, do not fill every pixel with small props. Enough dark value separation at outer sides for ivory interface text. Perspective eye level from customer side. No people, no animals, no figures, no Spine sprites, no text, no typography, no logos, no watermark, no screen frame, no UI.

### BlackstoneMap

Use case: stylized-concept. Asset type: original illustrated map background plate for a high-end hand-painted fantasy RPG UI, 16:9 landscape. World: Blackstone Harbor, a fortified medieval sea port perched over abyss catacombs. Draw a lavish but readable old adventurer's atlas spread on warm worn parchment. Intricate miniature ink-and-watercolor isometric harbor town in the left third: huddled slate roofs, fortified sea walls, tiny ships, old chapel. At center, an elegant winding subterranean aqueduct, old gates and broken stairs. Right third transitions into a circular arcane observatory above a deep dark well, ruined bridge and hints of gothic underground sanctum. Fine cartographic coastline contours, delicate sea hatching, architectural vignettes and watercolor teal washes, burgundy route accents only as a faint drawn path, antique gold details, etched border ornaments and aged paper texture. Premium original game illustration, meticulous brushwork, subdued contrast, parchment dominates with plenty of open light space for UI nodes and overlayed labels. No actual geographic place, no generated labels or words anywhere, no numerals, no icons implying clickable UI, no people, no character art, no Spine sprites, no modern features, no logos, no watermark. This is mood and world-map artwork; exact gameplay nodes will be drawn separately by code.

## 更新中文字体

新增 UI 文案后，在安装 `fonttools` 和 Noto Serif CJK 的开发环境运行：

```bash
python3 Tools~/build_iron_oath_font.py --source /path/to/NotoSerifCJK-Regular.ttc
```

脚本收集样例 C# 源文件的字符，保留版权与许可证，重命名为 Iron Oath Serif；游戏玩家不需要安装源字体或 Python。
