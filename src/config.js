'use strict';

const path = require('path');

module.exports = {
  // ---------- 服务 ----------
  PORT: 8091,
  HOST: '127.0.0.1',
  APP_NAME: '我的世界更新助手',
  APP_VERSION: '1.1.0',

  // ---------- PCL2 探测 ----------
  PCL2_DEFAULT_ROOTS: [
    'E:\\mc\\pcl2',
    'D:\\PCL2',
    'C:\\PCL2',
  ],
  PCL2_EXE_NAMES: ['Plain Craft Launcher 2.exe', 'Plain Craft Launcher.exe', 'PCL2.exe'],
  PCL2_SETUP_INI: path.join('PCL', 'Setup.ini'),
  PCL2_LOG_DIR: 'PCL',
  PCL2_LOG_FILES: ['Log1.txt', 'Log2.txt', 'Log3.txt', 'Log4.txt', 'Log5.txt'],
  PCL2_LOG_FOLDER_PATTERN: /有效的 Minecraft 文件夹：(.+?) > (.+)/,

  // ---------- Minecraft ----------
  MC_VERSIONS_DIR: 'versions',
  MC_PCL_INI: 'PCL.ini',

  /**
   * 工具只处理这四类内容，其余（journeymap / options.txt / servers.dat /
   * CustomSkinLoader / saves / logs / screenshots ...）一律不碰。
   * 这是硬性安全边界，不要随意放宽。
   */
  MANAGED_CATEGORIES: [
    { key: 'mods', dir: 'mods', label: '模组', defaultOn: true, byModId: true },
    { key: 'config', dir: 'config', label: '配置文件', defaultOn: false, byModId: false },
    { key: 'resourcepacks', dir: 'resourcepacks', label: '资源包', defaultOn: false, byModId: false },
    { key: 'shaderpacks', dir: 'shaderpacks', label: '光影包', defaultOn: false, byModId: false },
  ],

  /**
   * 分类目录「内部」的保护规则（相对分类目录的路径，支持 * 和 **）。
   *
   * config/ 里混着大量 mod 运行时生成的玩家数据——真实案例：
   *   config/fuji/backup/*.zip  是备份 mod 存的世界存档压缩包，一个几十上百 MB，
   *   跟着整合包走会把服主的备份塞给玩家，还会把玩家自己的备份判成"该删"。
   *   config/jei/world/server/<服务器名>/ 是玩家在各服务器的物品查询历史和书签。
   * 这些一律不列入对比，连显示都不显示。
   */
  CATEGORY_PROTECTED: {
    config: [
      'fuji/backup/**',        // 备份 mod 的存档压缩包
      'fuji/cache/**',
      '.puzzle_cache/**',
      'jei/world/**',          // JEI 按服务器存的书签 / 查询历史
      'crash_assistant/**',    // 崩溃报告
      'voicechat/**',          // 语音的麦克风与音量，跟本机硬件绑定
      'e4mc/**',               // 联机凭据
      'cape-provider/**',      // 披风，跟账号绑定
      '**/*.zip', '**/*.log', '**/*.log.gz',
      '**/*.db', '**/*.sqlite', '**/*.bak', '**/*.tmp',
    ],
    resourcepacks: ['**/*.tmp'],
    shaderpacks: ['**/*.tmp'],
  },

  /**
   * config 下单个文件超过这个大小就跳过。
   * 正常配置文件都是几 KB 到几百 KB，超过这个数的多半是数据/缓存/备份。
   */
  CONFIG_MAX_FILE: 8 * 1024 * 1024,

  /** 永不触碰的目录/文件（即使整合包里带了也跳过） */
  PROTECTED_ENTRIES: [
    'saves', 'logs', 'screenshots', 'crash-reports', 'cache', 'data', 'local',
    'downloads', 'journeymap', 'xaero', 'CustomSkinLoader', 'schematics',
    'options.txt', 'optionsof.txt', 'servers.dat', 'servers.dat_old',
    'usercache.json', 'usernamecache.json', 'realms_persistence.json',
    '.fabric', '.mods', '.mixin.out', 'PCL', 'PCL.ini',
  ],

  // ---------- 整合包 ----------
  /** mrpack 内容根 */
  MRPACK_OVERRIDES: 'overrides',
  MRPACK_INDEX: 'modrinth.index.json',
  /**
   * 小于该阈值的内层包直接读进内存（快约 3 倍）；超过则流式落临时文件。
   * 实测 214MB 的包走内存峰值 RSS 会顶到 580MB，走流式只有 ~150MB。
   * 128MB 这个位置能让绝大多数整合包走流式，把内存压住。
   */
  INMEMORY_THRESHOLD: 128 * 1024 * 1024,

  // ---------- 备份 ----------
  BACKUP_DIR_NAME: '备份',
  BACKUP_KEEP: 3,
  CHANGELOG_NAME: '更新日志.txt',
  MANIFEST_NAME: '.mcupdater-manifest.json',

  // ---------- 其他 ----------
  PUBLIC_DIR: path.join(__dirname, '..', 'public'),
  PARSE_CONCURRENCY: 8,
};
