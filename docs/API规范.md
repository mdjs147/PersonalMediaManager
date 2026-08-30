# PersonalMediaManager HTTP API 规范

> 版本：v0.1.0（与首个正式发版对齐）
> 最后更新：2026-06-12
> 基地址：`/api`（由 `ApiRoutePrefixConvention` 统一为所有控制器加 `api/` 前缀；控制器 `[Route(...)]` 本身不含前缀）
> 适用范围：本文按需求文档 §5.2 端点清单顺序，逐端点展开路径/Method/鉴权/请求体/响应/错误码。

> ⚠️ **以代码为准的免责声明（务必先读）**
> 本规范是**人工维护的核心域与稳定契约速查**，可能滞后于代码。**运行时的权威来源是 Scalar UI（`/scalar/v1`，读 `/openapi/v1.json`）+ 前端 `schema.d.ts`（由 openapi-typescript 自动生成）**。当本文与 Scalar / `schema.d.ts` 冲突时，**一律以后两者为准**。
>
> 2026-06-12 已按实际代码统一回改全文（路径 / 谓词 / id 位置 / `message` 固定 `"ok"` / 枚举值）。写操作 id 风格现状：
> - **新增**：直接 `POST /api/<resource>`（资源根，无 `/create` 段）；仅 account 沿用 `users/create`。
> - **修改 / 删除**：`POST /api/<resource>/update|delete`，**id 在请求体**。
> - **路径 id** 仅用于动作类端点（`/{id}/confirm`、`/{id}/test`、`/{id}/rescan`、`/{id}/stats` 等）与 account 的 `users/{id}/delete`。

---

## 〇、已实现但本文未详述的资源索引

> 下列端点已上线但本文未逐端点展开，契约详见 Scalar（`/scalar/v1`）：

| 资源 / 端点 | 用途 |
|---|---|
| `GET /api/system/version` | 极简版本号查询（供前端「关于」与快速探活） |
| `GET /api/system/update-check`、`POST /api/system/update-check/run` / `/test` / `/skip` | 客户端检查更新（读 GitHub Releases latest），v0.1.0 起的升级检查能力 |
| `POST /api/system/clear-history`、`POST /api/system/reset-config` | 清空处理历史 / 重置配置（高危维护操作） |
| `GET /api/dashboard/health` / `/tasks` / `/heatmap` / `/watch-folder-activity` | 仪表盘扩展：健康卡片 / 任务卡片 / 处理热力图 / 目录活跃度 |
| `GET /api/settings/parse-rules/builtin` | 内置兜底正则只读列表（不可编辑 / 禁用 / 删除） |
| `POST /api/settings/parse-rules/import` | 解析规则批量导入（multipart JSON 文件，`?mode=Merge/Replace`） |
| `/api/settings/parse-testcases`（CRUD + run / run-all / approve / triage / suggest-rule 等 14 端点） | 解析测试用例（回归样本库与规则建议） |
| `/api/settings/category-match-rules`（CRUD，写操作 id 在请求体） | 分类匹配规则（已从 categories 嵌套结构独立成资源） |
| `POST /api/history/{id}/reopen` | 把已终态记录重开回待确认队列 |
| `GET /api/health`、`GET /api/diag/boom` | 健康探针 / 异常处理自检（诊断用） |

---

## 一、通用约定

### 1.1 响应体

统一结构（CLAUDE.md §六 三码原则）：

```json
{
  "code": 0,
  "message": "ok",
  "data": {},
  "requestId": "5f1c8a3f-2c4e-4c2a-9c11-6f3a1b8a3c10"
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `code` | int | `0` 成功；`1000` 业务失败；`9000` 服务器/基础设施错误 |
| `message` | string | 人类可读说明；成功固定 `"ok"`，失败为中文原因（见 `ApiResponse.Success` / `Fail`） |
| `data` | object/null | 业务数据；失败时通常为 `null` |
| `requestId` | string | UUID，**所有响应必有**；由中间件生成，可被前端贴入工单 |

### 1.2 HTTP 谓词

- **仅使用 `GET` 与 `POST` 两个谓词**（CLAUDE.md / 需求文档 §5.1）
- `GET` —— 读取（含个别只读探测动作，如 `GET /{id}/test` 连通性测试）
- `POST` —— 所有写入：新增直接 `POST` 资源根；修改/删除走 `/update`、`/delete`（id 在请求体）；动作类端点动词放路径末段（`/{id}/confirm`、`/test`、`/cache/clear` 等）

### 1.3 鉴权

- 请求头 `Authorization: Bearer <jwt>`
- 未带 token 访问需鉴权接口 → `1000` + `"未登录或登录已过期"`
- token 角色不足 → `1000` + `"无操作权限"`
- 响应头 `X-Token-Refresh: <newJwt>`：当 `exp - now < 7 天` 时下发新 token（无感续签）

### 1.4 分页

- 请求：`?page=1&pageSize=20`
- 默认：`page=1`, `pageSize=20`，`pageSize` 上限 100
- 响应：

```json
{
  "code": 0,
  "message": "ok",
  "data": { "items": [], "total": 0, "page": 1, "pageSize": 20 },
  "requestId": "..."
}
```

### 1.5 错误响应统一示例

任何失败响应字段完整：

```json
{
  "code": 1000,
  "message": "用户名或密码错误",
  "data": null,
  "requestId": "5f1c8a3f-2c4e-4c2a-9c11-6f3a1b8a3c10"
}
```

### 1.6 时间格式

- 所有 `*At` 字段：ISO8601 UTC，如 `"2026-05-16T02:00:00Z"`
- 前端按用户本地时区展示

---

## 二、端点详情

---

### 2.1 认证（Auth）

#### 2.1.1 `POST /api/auth/login` — 登录

- **鉴权：** 否

**请求体：**

```json
{ "username": "admin", "password": "p@ssw0rd" }
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `username` | string | 是 | 登录名 |
| `password` | string | 是 | 明文密码（仅在 HTTPS 或局域网 HTTP 环境） |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6...",
    "expiresAt": "2026-06-15T02:00:00Z",
    "user": { "id": 1, "username": "admin", "role": "Admin" }
  },
  "requestId": "5f1c8a3f-2c4e-4c2a-9c11-6f3a1b8a3c10"
}
```

**可能的错误 message：**

- `1000` `"用户名或密码错误"`
- `1000` `"用户名或密码不能为空"`
- `1000` `"账号已禁用"`
- `9000` `"登录服务异常，请稍后重试"`

---

#### 2.1.2 `POST /api/auth/logout` — 登出

- **鉴权：** 是

**请求体：** 空

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**说明：** JWT 无服务端会话；登出仅记录审计 + 提示前端清理 localStorage。

**可能的错误 message：**

- `1000` `"未登录或登录已过期"`

---

#### 2.1.3 `GET /api/auth/me` — 当前用户信息

- **鉴权：** 是

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "id": 1,
    "username": "admin",
    "role": "Admin",
    "lastLoginAt": "2026-05-16T02:00:00Z"
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"未登录或登录已过期"`

---

### 2.2 初始化（Setup）

#### 2.2.1 `GET /api/setup/status` — 初始化进度

- **鉴权：** 否

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "completed": false,
    "hasAdmin": false,
    "hasTmdbKey": false,
    "hasAiProvider": false,
    "hasWatchFolder": false,
    "hasCategory": false
  },
  "requestId": "..."
}
```

**说明：** 当所有字段 `true` 且 `completed=true` 时，前端不再跳转向导。

---

#### 2.2.2 `POST /api/setup/admin` — 创建首个管理员

- **鉴权：** 否（仅在尚未存在任何 Admin 时可调用）

**请求体：**

```json
{ "username": "admin", "password": "p@ssw0rd" }
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `username` | string | 是 | 1–64 字符 |
| `password` | string | 是 | ≥ 6 字符 |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "id": 1, "username": "admin", "role": "Admin" },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"系统已存在管理员，禁止重复初始化"`
- `1000` `"用户名长度需为 1–64 字符"`
- `1000` `"密码至少 6 位"`
- `9000` `"创建管理员失败"`

---

#### 2.2.3 `POST /api/setup/complete` — 完成向导

- **鉴权：** 是（Admin）

**请求体：** 空

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "completed": true }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"未登录或登录已过期"`
- `1000` `"无操作权限"`
- `1000` `"尚未完成必要配置：缺少 TMDB Key / AI 提供商 / 监控目录 / 媒体分类"`

---

### 2.3 账号管理（Account）

#### 2.3.1 `GET /api/account/users` — 用户列表

- **鉴权：** Admin

**查询参数：** `page`, `pageSize`

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      { "id": 1, "username": "admin", "role": "Admin", "lastLoginAt": "2026-05-16T02:00:00Z", "createdAt": "2026-05-10T01:00:00Z" }
    ],
    "total": 1, "page": 1, "pageSize": 20
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"未登录或登录已过期"`
- `1000` `"无操作权限"`

---

#### 2.3.2 `POST /api/account/users/create` — 新增用户

- **鉴权：** Admin

**请求体：**

```json
{ "username": "viewer1", "password": "p@ssw0rd", "role": "Viewer" }
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `username` | string | 是 | 1–64 字符，全局唯一 |
| `password` | string | 是 | ≥ 6 字符 |
| `role` | string | 是 | `Admin` / `Viewer` |

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "id": 2, "username": "viewer1", "role": "Viewer" }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"用户名已存在"`
- `1000` `"密码至少 6 位"`
- `1000` `"角色取值非法（应为 Admin/Viewer）"`
- `1000` `"无操作权限"`

---

#### 2.3.3 `POST /api/account/users/{id}/delete` — 删除用户

- **鉴权：** Admin

**请求体：** 空

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"用户不存在"`
- `1000` `"不能删除当前登录用户"`
- `1000` `"不能删除最后一个管理员"`
- `1000` `"无操作权限"`

---

#### 2.3.4 `POST /api/account/password/change` — 修改自己密码

- **鉴权：** 是（任何登录用户）

**请求体：**

```json
{ "oldPassword": "p@ssw0rd", "newPassword": "newPwd123" }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"原密码不正确"`
- `1000` `"新密码至少 6 位"`
- `1000` `"新密码不能与原密码相同"`
- `1000` `"未登录或登录已过期"`

---

### 2.4 仪表盘（Dashboard）

#### 2.4.1 `GET /api/dashboard/stats` — 今日/总计/命中率

- **鉴权：** 否

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "today": { "processed": 12, "skipped": 1, "failed": 0, "review": 2 },
    "total": { "processed": 1058, "skipped": 23, "failed": 5 },
    "queue": { "review": 2, "running": 1, "pending": 0 },
    "parseSource": { "rule": 980, "ai": 65, "hybrid": 13 },
    "service": { "running": true, "uptimeSeconds": 86400 }
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `9000` `"统计数据读取失败"`

---

#### 2.4.2 `GET /api/dashboard/recent` — 最近处理

- **鉴权：** 否

**查询参数：** `limit`（默认 20，最大 100）

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "id": 123,
        "fileName": "Inception.2010.1080p.BluRay.mkv",
        "status": "Completed",
        "title": "盗梦空间",
        "year": 2010,
        "category": "电影",
        "targetPath": "D:/Media/Movies/盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv",
        "parseSource": "Rule",
        "archivedAt": "2026-05-16T02:00:00Z"
      }
    ]
  },
  "requestId": "..."
}
```

---

### 2.5 队列（Review）

#### 2.5.1 `GET /api/review` — 待确认列表

- **鉴权：** 否（查看）

**查询参数：** `page`, `pageSize`, `parseSource`（可选：`Rule`/`Ai`/`Hybrid`，枚举按成员名序列化）

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "id": 200,
        "fileName": "movie.unknown.mkv",
        "sourcePath": "F:/Downloads/movie.unknown.mkv",
        "parseSource": "Ai",
        "confidence": 0.42,
        "parsedInfo": "{\"title\":\"未识别\",\"type\":\"unknown\"}",
        "tmdbCandidates": [
          { "tmdbId": 27205, "title": "盗梦空间", "year": 2010, "posterUrl": "https://image.tmdb.org/.../poster.jpg" }
        ],
        "rowVersion": 3,
        "createdAt": "2026-05-16T01:30:00Z"
      }
    ],
    "total": 1, "page": 1, "pageSize": 20
  },
  "requestId": "..."
}
```

> `parsedInfo` 为 **JSON 原文字符串**（非嵌套对象），前端需自行 `JSON.parse`；`rowVersion`（long）供后续 confirm / ignore 做乐观并发。

---

#### 2.5.2 `POST /api/review/{id}/confirm` — 确认归档

- **鉴权：** Admin

**请求体：**

```json
{
  "tmdbId": 27205,
  "mediaType": "movie",
  "categoryId": 1,
  "title": "盗梦空间",
  "year": 2010,
  "season": null,
  "episode": null,
  "rowVersion": 3
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `tmdbId` | int | 是 | 选定的 TMDB ID |
| `mediaType` | string | 是 | `movie` / `tv` |
| `categoryId` | int | 是 | 分类 ID |
| `title` / `year` / `season` / `episode` | — | 否 | 用户可覆盖解析结果 |
| `rowVersion` | long | 是 | 乐观并发：必须与列表读到的一致 |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "id": 200, "status": "Archiving", "rowVersion": 4 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"记录不存在"`
- `1000` `"记录已被其他用户修改，请刷新"` （并发冲突）
- `1000` `"分类不存在"`
- `1000` `"剧集必须填写季号与集号"`
- `1000` `"TMDB ID 无效或无法查询"`
- `1000` `"无操作权限"`
- `9000` `"提交归档任务失败"`

---

#### 2.5.3 `POST /api/review/{id}/ignore` — 忽略

- **鉴权：** Admin

**请求体：**

```json
{ "rowVersion": 3, "reason": "测试样本" }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "id": 200, "status": "Ignored" }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"记录不存在"`
- `1000` `"记录已被其他用户修改，请刷新"`
- `1000` `"无操作权限"`

---

#### 2.5.4 `POST /api/review/batch-confirm` — 批量确认

- **鉴权：** Admin

**请求体：**

```json
{
  "items": [
    { "id": 200, "tmdbId": 27205, "mediaType": "movie", "categoryId": 1, "rowVersion": 3 },
    { "id": 201, "tmdbId": 1396,  "mediaType": "tv",    "categoryId": 2, "season": 1, "episode": 1, "rowVersion": 2 }
  ]
}
```

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "succeeded": [200, 201],
    "failed": []
  },
  "requestId": "..."
}
```

**部分失败也返回 200 + code=0**，由 `failed` 数组单独承载失败明细：

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "succeeded": [200],
    "failed": [
      { "id": 201, "message": "记录已被其他用户修改，请刷新" }
    ]
  },
  "requestId": "..."
}
```

**可能的错误 message（整体失败）：**

- `1000` `"items 不能为空"`
- `1000` `"items 数量超过上限 50"`
- `1000` `"无操作权限"`

---

#### 2.5.5 `GET /api/review/{id}/tmdb-search` — 重新搜索 TMDB

- **鉴权：** Admin

**查询参数：** `query`（搜索词，必填）、`type`（`movie`/`tv`/`both`，默认 `both`）、`year`（可选）

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "tmdbId": 27205, "mediaType": "movie", "title": "盗梦空间",
        "originalTitle": "Inception", "year": 2010,
        "posterUrl": "https://image.tmdb.org/.../poster.jpg",
        "originCountry": ["US"], "popularity": 84.5
      }
    ]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"搜索词不能为空"`
- `1000` `"记录不存在"`
- `1000` `"无操作权限"`
- `9000` `"TMDB 服务异常"`

---

#### 2.5.6 `POST /api/review/{id}/bind-tmdb` — 手动指定 TmdbId

- **鉴权：** Admin

**请求体：**

```json
{ "tmdbId": 27205, "mediaType": "movie", "rowVersion": 3 }
```

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "id": 200,
    "tmdbId": 27205,
    "title": "盗梦空间",
    "year": 2010,
    "rowVersion": 4
  },
  "requestId": "..."
}
```

**说明：** 仅绑定 + 拉取 TMDB 元数据更新到记录，**不**立即触发归档；归档仍需调用 `/api/review/{id}/confirm`。

**可能的错误 message：**

- `1000` `"记录不存在"`
- `1000` `"记录已被其他用户修改，请刷新"`
- `1000` `"TMDB ID 无效或无法查询"`
- `1000` `"mediaType 取值非法（应为 movie/tv）"`
- `9000` `"TMDB 服务异常"`

---

### 2.6 历史（History）

#### 2.6.1 `GET /api/history` — 历史列表（过滤）

- **鉴权：** 否

**查询参数：**

| 参数 | 类型 | 说明 |
|---|---|---|
| `page` / `pageSize` | int | 分页 |
| `status` | string | `Completed` / `Skipped` / `Ignored` / `Cancelled` / `Failed` |
| `parseSource` | string | `Rule` / `Ai` / `Hybrid`（枚举按成员名序列化） |
| `categoryId` | int | 分类筛选 |
| `from` / `to` | string | ISO8601 时间区间，按 `ArchivedAt` |
| `keyword` | string | 文件名/标题模糊匹配 |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "id": 123,
        "fileName": "Inception.2010.1080p.BluRay.mkv",
        "title": "盗梦空间", "year": 2010,
        "status": "Completed",
        "parseSource": "Rule",
        "confidence": 0.95,
        "category": "电影",
        "targetPath": "D:/Media/Movies/盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv",
        "archivedAt": "2026-05-16T02:00:00Z"
      }
    ],
    "total": 1058, "page": 1, "pageSize": 20
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"分页参数非法"`
- `1000` `"时间区间格式错误"`

---

#### 2.6.2 `POST /api/history/{id}/cancel` — 取消任务

- **鉴权：** Admin
- **请求体：** 空
- **适用状态：** `Detected` / `Queued` / `Parsing` / `TmdbMatching` / `AiParsing` / `TmdbRematching` / `Classifying`

正在处理的任务会先停止独立处理令牌，待管线退出后再落 `Cancelled`；`Archiving` 可能已经移动文件，为保证磁盘与数据库一致性不允许取消。

```json
{ "code": 0, "message": "ok", "data": { "id": 123, "status": "Cancelled" }, "requestId": "..." }
```

---

#### 2.6.3 `POST /api/history/{id}/rescan` — 重新处理

- **鉴权：** Admin

**请求体：** 空

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "id": 123, "status": "Queued" }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"记录不存在"`
- `1000` `"源文件已不存在，无法重新处理"`
- `1000` `"任务正在处理中，请稍后重试"`
- `1000` `"无操作权限"`
- `9000` `"重投任务失败"`

---

### 2.7 日志（Logs）

#### 2.7.1 `GET /api/logs` — 历史日志（过滤）

- **鉴权：** 否

**查询参数：**

| 参数 | 类型 | 说明 |
|---|---|---|
| `page` / `pageSize` | int | 分页（`pageSize` 上限 200，日志域放宽） |
| `level` | string | `Verbose` / `Debug` / `Information` / `Warning` / `Error` / `Fatal` |
| `from` / `to` | string | ISO8601 时间区间 |
| `keyword` | string | 消息文本模糊匹配 |
| `source` | string | 日志来源类名/category |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "timestamp": "2026-05-16T02:00:00.123Z",
        "level": "Information",
        "source": "PersonalMediaManager.Host.HostedServices.FileWatcherWorker",
        "message": "检测到新文件：盗梦空间.2010.1080p.mkv，已入队等待处理"
      }
    ],
    "total": 12345, "page": 1, "pageSize": 50
  },
  "requestId": "..."
}
```

**说明：** 日志走文件分页读取，不入库；`total` 为粗略估值，仅供前端展示进度。

**可能的错误 message：**

- `1000` `"分页参数非法"`
- `1000` `"日志级别取值非法"`
- `9000` `"日志文件读取失败"`

---

### 2.8 扫描（Scan）

#### 2.8.1 `POST /api/scan/trigger` — 手动全量扫描

- **鉴权：** Admin

**请求体：** 空

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "scanId": "5f1c8a3f...", "folderCount": 3 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"已有扫描在进行中"`
- `1000` `"未配置任何监控目录"`
- `1000` `"无操作权限"`
- `9000` `"触发扫描失败"`

---

#### 2.8.2 `POST /api/scan/folder/{id}` — 单目录扫描

- **鉴权：** Admin

**请求体：** 空

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "scanId": "...", "folderId": 1, "path": "F:/Downloads" },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"监控目录不存在"`
- `1000` `"目录已禁用"`
- `1000` `"目录不可达（网络共享异常）"`
- `1000` `"已有扫描在进行中"`
- `1000` `"无操作权限"`

---

### 2.9 设置-通用（Settings: General）

#### 2.9.1 `GET /api/settings/general` — 读取通用配置

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "webPort": 7288,
    "fileOperation": "Move",
    "cleanEmptyDir": false,
    "scanIntervalHours": 12,
    "stabilitySecondsBeforeReady": 5,
    "logLevel": "Information",
    "parseConfidenceThreshold": 0.6,
    "aiConfidenceThreshold": 0.7
  },
  "requestId": "..."
}
```

---

#### 2.9.2 `POST /api/settings/general/update` — 更新通用配置

- **鉴权：** Admin

**请求体：** 同 GET 的 `data`（字段全部可选，按存在性更新）

```json
{
  "webPort": 7288,
  "fileOperation": "Copy",
  "cleanEmptyDir": true,
  "scanIntervalHours": 6
}
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "restartRequired": true }, "requestId": "..." }
```

**说明：** 修改 `webPort` 等需重启才生效的字段 → `restartRequired = true`。

**可能的错误 message：**

- `1000` `"端口范围必须在 1024–65535"`
- `1000` `"fileOperation 取值非法（应为 Move/Copy）"`
- `1000` `"scanIntervalHours 必须为正整数"`
- `1000` `"置信度阈值必须在 0.0–1.0"`
- `1000` `"无操作权限"`

---

### 2.10 监控目录（Watch Folders）

> 控制器 `WatchFoldersController`，`[Route("settings/watch/folders")]` → 实际前缀 **`/api/settings/watch/folders`**（注意是 `watch/folders` 两段，不是 `watch-folders`）。
> 新增为 POST 资源根；update / delete 的 **id 在请求体**；连通性测试为 **GET**（只读探测）。仅 Admin。

#### 2.10.1 `GET /api/settings/watch/folders` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    {
      "id": 1,
      "path": "F:/Downloads",
      "alias": "下载中转",
      "isTransit": true,
      "isNetworkShare": false,
      "enabled": true,
      "priority": 100,
      "lastScanAt": "2026-05-16T01:00:00Z",
      "lastReachableAt": null,
      "createdAt": "...",
      "updatedAt": "..."
    }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `priority` 升序。

---

#### 2.10.2 `POST /api/settings/watch/folders` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体：**

```json
{
  "path": "F:/Downloads",
  "alias": "下载中转",
  "isTransit": true,
  "isNetworkShare": false,
  "enabled": true,
  "priority": 100
}
```

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` 路径为空 / 长度超限 / 已存在 / 优先级越界 / 别名超长
- `1000` `"无操作权限"`

---

#### 2.10.3 `POST /api/settings/watch/folders/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`：

```json
{ "id": 1, "path": "F:/Downloads", "alias": "下载中转 2", "isTransit": false, "isNetworkShare": false, "enabled": true, "priority": 50 }
```

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"监控目录不存在"`
- `1000` 路径与其它目录冲突 / 校验失败
- `1000` `"无操作权限"`

---

#### 2.10.4 `POST /api/settings/watch/folders/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"监控目录不存在"`
- `1000` 目录下尚有未完成任务（in-flight Media_Item），先处理再删
- `1000` `"无操作权限"`

---

#### 2.10.5 `GET /api/settings/watch/folders/{id}/test` — 测试可访问性

- **鉴权：** Admin（**GET**，只读探测，id 在路径）

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "reachable": true, "directoryExists": true, "sampleEntry": "F:/Downloads/foo.mkv", "errorMessage": null },
  "requestId": "..."
}
```

**说明：** 目录不存在 / 读取失败不抛 `1000`，而是 `reachable=false` 且 `errorMessage` 携带原因。

**可能的错误 message：**

- `1000` `"监控目录不存在"`（Id 无效）
- `9000` `"测试过程异常"`

---

### 2.11 媒体分类（Categories）

> 控制器 `CategoriesController`，`[Route("settings/categories")]` → `/api/settings/categories`。
> 实体为**平铺结构**（不再嵌套 matchRules）；**分类匹配规则已独立成资源 `/api/settings/category-match-rules`**（CRUD，写操作 id 在请求体，见 §〇 索引）。
> 新增为 POST 资源根；update / delete 的 **id 在请求体**。仅 Admin。

#### 2.11.1 `GET /api/settings/categories` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    {
      "id": 1, "name": "电影", "mediaType": "Movie",
      "targetRoot": "D:/Plex/Movies", "priority": 100,
      "description": null, "createdAt": "...", "updatedAt": "..."
    }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `priority` 升序；匹配规则另查 `category-match-rules`。

---

#### 2.11.2 `POST /api/settings/categories` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体：**

```json
{ "name": "动漫", "mediaType": "Tv", "targetRoot": "D:/Plex/Anime", "priority": 100, "description": null }
```

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` `"分类名已存在"`
- `1000` `"mediaType 取值非法（应为 Movie/Tv/Both）"`
- `1000` `"目标根目录不能为空"`
- `1000` 优先级越界 / 校验失败
- `1000` `"无操作权限"`

---

#### 2.11.3 `POST /api/settings/categories/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`：

```json
{ "id": 1, "name": "电影改", "mediaType": "Movie", "targetRoot": "D:/Plex/Movies2", "priority": 50, "description": null }
```

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"分类不存在"`
- `1000` `"分类名已存在"`
- `1000` `"无操作权限"`

---

#### 2.11.4 `POST /api/settings/categories/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**说明：** 删除成功时关联匹配规则由 DB CASCADE 自动清理。

**可能的错误 message：**

- `1000` `"分类不存在"`
- `1000` `"该分类仍被历史记录引用，请先迁移或忽略相关记录"`
- `1000` `"无操作权限"`

---

### 2.12 解析规则（Parse Rules）

> 控制器 `ParseRulesController`，`[Route("settings/parse-rules")]` → `/api/settings/parse-rules`。
> 新增为 POST 资源根；update / delete 的 **id 在请求体**；保存时 `Regex(Compiled, Timeout=500ms)` 试编译，失败 → 1000。
> 另有 `GET builtin`（内置兜底正则只读）与 `POST import`（批量导入），见 §〇 索引。仅 Admin。

#### 2.12.1 `GET /api/settings/parse-rules` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    {
      "id": 1, "name": "国产剧 ZeroBureau 命名",
      "scope": "FileName",
      "pattern": "^(?<title>[^.]+)\\.S(?<season>\\d+)E(?<episode>\\d+)",
      "defaultType": "tv", "forceType": false,
      "priority": 100, "confidenceBonus": 0.1, "enabled": true,
      "description": "团队 ZeroBureau 默认命名",
      "createdAt": "...", "updatedAt": "..."
    }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `priority` 升序。

---

#### 2.12.2 `POST /api/settings/parse-rules` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体（Pattern 推荐命名捕获 title / year / season / episode）：**

```json
{
  "name": "国产剧 ZeroBureau 命名",
  "scope": "FileName",
  "pattern": "^(?<title>[^.]+)\\.S(?<season>\\d+)E(?<episode>\\d+)",
  "defaultType": "tv",
  "forceType": false,
  "priority": 100,
  "confidenceBonus": 0.1,
  "enabled": true,
  "description": "团队 ZeroBureau 默认命名"
}
```

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` `"规则名已存在"`
- `1000` `"scope 取值非法（应为 FileName/ParentFolder/FullPath）"`
- `1000` 正则编译失败（含疑似 ReDoS 超时）
- `1000` `"defaultType 取值非法（应为 movie/tv/空）"`
- `1000` 置信度加成越界
- `1000` `"无操作权限"`

---

#### 2.12.3 `POST /api/settings/parse-rules/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"规则不存在"`
- 其余同新增

---

#### 2.12.4 `POST /api/settings/parse-rules/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"规则不存在"`
- `1000` `"无操作权限"`

---

#### 2.12.5 `POST /api/settings/parse-rules/test` — 即席测试

- **鉴权：** Admin

**请求体（仅 `pattern` + `sample`，不读 DB、不需要 id）：**

```json
{
  "pattern": "^(?<title>[^.]+)\\.S(?<season>\\d+)E(?<episode>\\d+)",
  "sample": "Breaking.Bad.S01E01.1080p.BluRay.mkv"
}
```

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "matched": true,
    "groups": { "title": "Breaking.Bad", "season": "01", "episode": "01" },
    "elapsedMilliseconds": 0.123,
    "errorMessage": null
  },
  "requestId": "..."
}
```

**说明：** 纯内存对 `(pattern, sample)` 跑 `Regex.Match`，返回命名捕获组；正则编译失败 / 匹配超时不抛 `1000`，而是 `matched=false` 且 `errorMessage` 携带原因。

**可能的错误 message：**

- `1000` pattern 为空 / sample 为 null
- `1000` `"无操作权限"`

---

### 2.13 AI 提供商（AI Providers）

> 控制器 `ParseAiProvidersController`，`[Route("settings/ai-providers")]` → `/api/settings/ai-providers`。
> 新增为 POST 资源根；update / delete / test / enable / reset-quota 的 **id 全部在请求体**（仅监控类 `{id}/stats`、`{id}/logs` 的 id 在路径，见 §2.20）。仅 Admin。

#### 2.13.1 `GET /api/settings/ai-providers` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    {
      "id": 1, "name": "主-Qwen", "type": "Qwen",
      "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
      "hasApiKey": true,
      "model": "qwen-plus",
      "isPrimary": true, "priority": 100,
      "enabled": true, "disabledUntil": null,
      "timeoutSeconds": 30, "extraOptions": null,
      "createdAt": "...", "updatedAt": "..."
    }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `isPrimary` 优先 + `priority` 升序；`hasApiKey` 为布尔脱敏字段，明文 Key **从不返回**。

---

#### 2.13.2 `POST /api/settings/ai-providers` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体：**

```json
{
  "name": "主-Qwen",
  "type": "Qwen",
  "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
  "apiKey": "sk-xxx",
  "model": "qwen-plus",
  "isPrimary": true,
  "priority": 100,
  "enabled": true,
  "timeoutSeconds": 30,
  "extraOptions": null
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `type` | 是 | `Ollama` / `Qwen` / `DeepSeek` / `OpenAICompatible` |
| `apiKey` | 否 | Ollama 可省略，其余必填；明文上送，后端 DataProtection 加密存库 |
| `isPrimary` | 否 | 全表至多 1 个为 true，置 true 自动把旧主降级 |
| `extraOptions` | 否 | JSON 字符串（非法 JSON → 1000） |

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` `"提供商名已存在"`
- `1000` `"type 取值非法"`
- `1000` BaseUrl 非 http(s) URL
- `1000` `"非 Ollama 类型必须填写 ApiKey"`
- `1000` timeoutSeconds 越界 / ExtraOptions 非 JSON
- `1000` `"无操作权限"`

---

#### 2.13.3 `POST /api/settings/ai-providers/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`。`apiKey` 三态：`null` = 保持不变（不重发密钥也能改其他字段）；`""` = 清空（仅 Ollama 允许）；非空 = 重新加密。

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"提供商不存在"`
- 其余同新增

---

#### 2.13.4 `POST /api/settings/ai-providers/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"提供商不存在"`
- `1000` `"无操作权限"`

---

#### 2.13.5 `POST /api/settings/ai-providers/test` — 测连接

- **鉴权：** Admin
- **id 在请求体**（用已保存配置真发 1 次最小请求）

**请求体：**

```json
{ "id": 1 }
```

**探测策略：** `Ollama` → `GET /api/tags`；其他类型 → `POST /v1/chat/completions`（`messages=[ping]`，`max_tokens=1`）。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "success": true, "httpStatus": 200, "elapsedMilliseconds": 123.4, "errorMessage": null, "responseSnippet": "..." },
  "requestId": "..."
}
```

**说明：** 网络 / 鉴权失败不抛 `1000`，而是 `success=false` 且 `errorMessage` 携带原因（HTTP 链路结果看 `data.success`）。

**可能的错误 message：**

- `1000` `"提供商不存在"`（Id 无效）
- `9000` `"测试过程异常"`

---

#### 2.13.6 `POST /api/settings/ai-providers/enable` — 手动解禁

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**说明：** 仅清空 `DisabledUntil`（熔断解禁）；**不**改动 `Enabled` 标志，保留用户显式禁用语义；也**不**清除 `QuotaExceededAt`（配额禁用不被健康解禁绕过，须放宽限额或走 reset-quota）。

**可能的错误 message：**

- `1000` `"提供商不存在"`
- `1000` `"无操作权限"`

---

#### 2.13.7 `POST /api/settings/ai-providers/reset-quota` — 重置套餐用量

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**说明：** 清零 `QuotaUsedCalls` / `QuotaUsedTokens` 并清除 `QuotaExceededAt`（解除配额禁用），用于开始新套餐周期 / 续购；限额配置（`QuotaCallLimit` / `QuotaTokenLimit` / `QuotaExpiresAt`）与 `Enabled` / `DisabledUntil` 均不改动（到期时间调整走 update）。

**可能的错误 message：**

- `1000` `"提供商不存在"`
- `1000` `"无操作权限"`

---

### 2.14 TMDB

#### 2.14.1 `GET /api/settings/tmdb` — 读取配置

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "hasApiKey": true,
    "language": "zh-CN",
    "fallbackLanguage": "en-US",
    "candidateThreshold": 3,
    "rateLimitPerSecond": 40,
    "metadataCacheHours": 24,
    "searchCacheMinutes": 60,
    "scoreWeights": { "title": 0.5, "year": 0.3, "popularity": 0.1, "language": 0.1 }
  },
  "requestId": "..."
}
```

---

#### 2.14.2 `POST /api/settings/tmdb/update` — 更新配置

- **鉴权：** Admin

**请求体：**

```json
{
  "apiKey": "tmdb-xxx",
  "language": "zh-CN",
  "fallbackLanguage": "en-US",
  "candidateThreshold": 3,
  "rateLimitPerSecond": 40,
  "metadataCacheHours": 24,
  "searchCacheMinutes": 60,
  "scoreWeights": { "title": 0.5, "year": 0.3, "popularity": 0.1, "language": 0.1 }
}
```

> `apiKey` 不传则保留原值；权重之和不强制为 1（应用层归一化）。

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"language 格式非法（应为 ISO 639-1，如 zh-CN）"`
- `1000` `"candidateThreshold 必须为正整数"`
- `1000` `"rateLimitPerSecond 必须在 1–100"`
- `1000` `"权重必须为 0.0–1.0"`
- `1000` `"无操作权限"`

---

#### 2.14.3 `POST /api/settings/tmdb/test` — 测连接

- **鉴权：** Admin

**请求体：** 空；或临时覆盖 `{ "apiKey": "tmdb-xxx" }`

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "ok": true, "latencyMs": 153 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"API Key 无效"`
- `1000` `"连接超时"`
- `9000` `"TMDB 服务异常"`

---

#### 2.14.4 `POST /api/settings/tmdb/cache/clear` — 清空缓存

- **鉴权：** Admin

**请求体：**

```json
{ "scope": "all" }
```

| 字段 | 说明 |
|---|---|
| `scope` | `all` / `metadata` / `search` / `poster`，默认 `all` |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "metadataRemoved": 1024, "searchRemoved": 88, "posterRemoved": 532 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"scope 取值非法"`
- `9000` `"清理缓存失败"`

---

### 2.15 忽略规则（Ignore Rules）

> 控制器 `WatchIgnoreRulesController`，`[Route("settings/watch/ignore-rules")]` → 实际前缀 **`/api/settings/watch/ignore-rules`**（注意带 `watch/` 段）。
> 新增为 POST 资源根；update / delete 的 **id 在请求体**；初始 16 条扩展名种子（`.part` / `.torrent` / `.xltd` 等）由 Migration 注入。仅 Admin。

#### 2.15.1 `GET /api/settings/watch/ignore-rules` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    { "id": 1, "type": "Extension", "pattern": ".part", "description": "默认忽略下载中临时文件", "enabled": true, "createdAt": "...", "updatedAt": "..." }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `type` / `pattern` 排序。

---

#### 2.15.2 `POST /api/settings/watch/ignore-rules` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体：**

```json
{ "type": "Extension", "pattern": ".aria2", "description": "aria2 下载中", "enabled": true }
```

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` `"type 取值非法（应为 Extension/Keyword）"`
- `1000` `"pattern 不能为空"`
- `1000` `"扩展名必须以 . 开头"`（Extension 类型）
- `1000` pattern 含空白或路径分隔符
- `1000` `"同类型下 pattern 已存在"`
- `1000` `"无操作权限"`

---

#### 2.15.3 `POST /api/settings/watch/ignore-rules/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`：

```json
{ "id": 1, "type": "Extension", "pattern": ".part", "description": "新备注", "enabled": false }
```

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"规则不存在"`
- 其余同新增

---

#### 2.15.4 `POST /api/settings/watch/ignore-rules/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"规则不存在"`
- `1000` `"无操作权限"`

---

### 2.16 Webhook

> 控制器 `WebhooksController`，`[Route("settings/webhooks")]` → `/api/settings/webhooks`。
> 新增为 POST 资源根；update / delete 的 **id 在请求体**；`{id}/test`、`{id}/retry/{deliveryId}` 的 id 在路径；出站日志为全局 `GET deliveries`（订阅过滤走查询参数）。仅 Admin。

#### 2.16.0 总开关 — `GET` / `POST /api/settings/webhooks/enabled`

- **鉴权：** Admin
- **语义：** 全局总开关（`System_Setting.Webhook_Enabled`，默认 `false`）。关闭时归档**不产生任何 `Webhook_Delivery`**、不入队；已入库的待投递仍由 OutboxWorker 处理完。每订阅 `enabled` 仅在总开关开启后才生效。

**GET 成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "enabled": false }, "requestId": "..." }
```

**POST 请求体：**

```json
{ "enabled": true }
```

**POST 成功响应：** 同 GET 形状（返回设置后的最新状态）。

---

#### 2.16.1 `GET /api/settings/webhooks` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    {
      "id": 1, "name": "Telegram 转发",
      "url": "https://bot.example.com/hook",
      "hasSecret": true,
      "events": ["media.archived", "media.failed"],
      "enabled": true,
      "timeoutSeconds": 10,
      "createdAt": "...", "updatedAt": "..."
    }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封）；`hasSecret` 为布尔脱敏，明文 Secret 不返回。

---

#### 2.16.2 `POST /api/settings/webhooks` — 新增

- **鉴权：** Admin（POST 资源根，无 `/create` 段）

**请求体：**

```json
{
  "name": "Telegram 转发",
  "url": "https://bot.example.com/hook",
  "secret": "shared-secret-string",
  "events": ["media.archived", "media.failed", "review.created"],
  "enabled": true,
  "timeoutSeconds": 10
}
```

| 字段 | 说明 |
|---|---|
| `events` | 数组，可选值：`media.archived` / `media.skipped` / `media.failed` / `review.created` / `backup.failed`（定时备份失败） / `disk.low`（归档盘将满） / `share.unreachable`（网络盘掉线） / `ai.all_unavailable`（AI 全部不可用） / `ai.provider_quota_exceeded`（AI 提供商套餐用量超限自动禁用） |

**成功响应：** 返回创建后的完整实体（同列表项形状）。

**可能的错误 message：**

- `1000` `"订阅名已存在"`
- `1000` `"URL 必须为 http/https"`
- `1000` `"events 不能为空"`
- `1000` `"events 包含未知事件类型"`
- `1000` `"timeoutSeconds 必须在 1–60"`
- `1000` `"无操作权限"`

---

#### 2.16.3 `POST /api/settings/webhooks/update` — 修改

- **鉴权：** Admin
- **id 在请求体**

**请求体：** 同新增 + `id`。`secret` 三态：`null` = 保持不变；`""` = 清空；非空 = 替换。

**成功响应：** 返回更新后的完整实体。

**可能的错误 message：**

- `1000` `"订阅不存在"`
- 其余同新增

---

#### 2.16.4 `POST /api/settings/webhooks/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**说明：** 删除时该订阅的 `Webhook_Delivery` 由 DB FK CASCADE 自动清理。

**可能的错误 message：**

- `1000` `"订阅不存在"`
- `1000` `"无操作权限"`

---

#### 2.16.5 `GET /api/settings/webhooks/deliveries` — 出站日志

- **鉴权：** Admin
- **全局路径**（无 `{id}` 段，订阅过滤走查询参数）

**查询参数：**

| 参数 | 类型 | 说明 |
|---|---|---|
| `subscriptionId` | long? | 可选，仅看某订阅 |
| `status` | string? | `Pending` / `Retrying` / `Success` / `Failed` |
| `skip` / `take` | int | 游标式分页：`skip` 默认 0；`take` 默认 50，上限 200（**非** `page`/`pageSize`） |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "id": 88,
        "subscriptionId": 1,
        "event": "media.archived",
        "status": "Success",
        "attempts": 1,
        "lastTriedAt": "2026-05-16T02:01:00Z",
        "nextRetryAt": null,
        "lastStatusCode": 200,
        "lastError": null,
        "requestId": "...",
        "createdAt": "...", "updatedAt": "..."
      }
    ],
    "total": 50
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"Skip 必须 ≥ 0"`（`take ≤ 0` 走默认 50；`take > 200` 截断至 200；`subscriptionId` 不存在不报错，返回空 `items`）

> **出站 payload 语义补充**（投递正文结构详见《需求文档》§3.10，此处仅记两处事件级差异）：
> - `media.archived`：`data` 含降级标记 `metadataPending`（bool）与 `warnings`（string[]）——视频已落地但 nfo / 海报等元数据步骤部分失败时 `metadataPending=true` 且 `warnings` 列失败明细；正常归档 `metadataPending=false`、`warnings` 为空。
> - `media.skipped`：`targetPath` 仅指本记录归档产物，两种触发场景均未产生产物，故为 `null`——内容去重命中未做任何文件操作（已存在副本以处理时间线的 `duplicateOf` 标识）；归档同名冲突不外发他人文件的路径（冲突目标仅记入时间线 detail）。

---

#### 2.16.6 `POST /api/settings/webhooks/{id}/retry/{deliveryId}` — 手动重试

- **鉴权：** Admin

**请求体：** 空

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "deliveryId": 88, "status": "Pending", "attempts": 0 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"订阅不存在"`
- `1000` `"投递记录不存在"`
- `1000` `"投递记录已成功，无需重试"`
- `1000` `"投递记录归属不匹配"`
- `1000` `"无操作权限"`
- `9000` `"重试入队失败"`

---

#### 2.16.7 `POST /api/settings/webhooks/{id}/test` — 测试发送

- **鉴权：** Admin（id 在路径）

**请求体：** 空

**说明：** 同步向订阅 URL 发送一次 `test.ping` 事件（与生产投递同形：HMAC 签名 + 4 个 `X-PMM-*` 头）；**不写 `Webhook_Delivery` 表**，避免污染生产投递历史。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "success": true, "httpStatus": 200, "elapsedMilliseconds": 124.3, "event": "test.ping", "requestId": "...", "errorMessage": null, "responseSnippet": "{...}" },
  "requestId": "..."
}
```

**说明：** HTTP 链路失败不抛 `1000`，而是 `success=false` 且 `errorMessage` 携带原因。

**可能的错误 message：**

- `1000` `"订阅不存在"`
- `1000` Secret 解密失败
- `1000` `"无操作权限"`

---

### 2.17 系统（System）

#### 2.17.1 `POST /api/system/export` — 导出 db + 配置

- **鉴权：** Admin

**响应：** `Content-Type: application/zip`，二进制 zip 流；文件名 `pmm-export-yyyyMMddHHmmss.zip`。

ZIP 内含：

```
pmm.db                  # 当前 SQLite 在线热备副本
appsettings.json
keys/                   # DataProtection 密钥环
manifest.json           # 版本、平台、导出时间
```

**失败响应（仍按统一 JSON 返回）：**

```json
{ "code": 9000, "message": "导出失败：磁盘写入异常", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"无操作权限"`
- `9000` `"数据库热备失败"`
- `9000` `"导出失败：磁盘写入异常"`

---

#### 2.17.2 `POST /api/system/import` — 导入

- **鉴权：** Admin

**请求：** `multipart/form-data`，单字段 `file`（zip 文件）

**机制（「暂存 + 启动期换库」，与 §2.17.6 restore 共用）：** 本端点仅校验 zip（防 Zip Slip、校验 `pmm.db` 为合法 SQLite）后把新库写到 `<db>.import-pending` 并合并 DataProtection 密钥环，**不在运行中热替换主库**（WAL 模式下热替换会被旧 WAL 回写覆盖 → 导入不生效）；真正换库在下次启动开库前原子完成，旧库自动备份为 `pmm.db.preimport-*`。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "success": true,
    "stagedPath": "...\\pmm.db.import-pending",
    "requiresRestart": true,
    "message": "导入包已暂存，请完整退出并重启应用以完成换库（重启时旧库会自动备份为 pmm.db.preimport-*）"
  },
  "requestId": "..."
}
```

**说明：** 前端收到 `requiresRestart=true` 应引导用户完整退出并重启程序。

**可能的错误 message：**

- `1000` `"未上传文件"`
- `1000` zip 格式错误
- `1000` Zip Slip 守卫触发
- `1000` 缺 `pmm.db`
- `1000` `"导入包的 pmm.db 不是合法的 SQLite 文件"`
- `1000` `"无操作权限"`
- `9000` `"文件 IO 失败"`

---

#### 2.17.3 `GET /api/system/info` — 版本、运行时间、平台

- **鉴权：** 否

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "version": "0.1.0",
    "platform": "Windows",
    "osVersion": "10.0.26200",
    "runtime": ".NET 10.0.0",
    "startedAt": "2026-05-16T00:00:00Z",
    "uptimeSeconds": 7200,
    "port": 7288,
    "dbPath": "C:/Users/.../AppData/Local/PersonalMediaManager/pmm.db"
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `9000` `"系统信息读取失败"`

---

#### 2.17.4 `POST /api/system/backup` — 立即备份

- **鉴权：** Admin

对数据库做一次 `VACUUM INTO` 在线快照 + 密钥环打包，落 `<数据目录>/backups/pmm-backup-yyyyMMddHHmmss.zip`，并按 `Backup_RetainCount` 清理旧备份。手动触发忽略 `Backup_Enabled` 开关（仅定时备份看开关）。请求体：空。

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "performed": true, "fileName": "pmm-backup-20260606040000.zip", "sizeBytes": 1234567, "prunedCount": 1 }, "requestId": "..." }
```

**可能的错误 message：**

- `9000` `"VACUUM INTO 失败 / 文件 IO 失败"`

---

#### 2.17.5 `GET /api/system/backups` — 备份列表

- **鉴权：** Admin

列出 `<数据目录>/backups/` 下全部备份 zip（自动 + 手动），按时间倒序，供「从备份恢复」选择恢复点。

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": [ { "fileName": "pmm-backup-20260606040000.zip", "sizeBytes": 1234567, "createdAt": "2026-06-06T04:00:00Z" } ], "requestId": "..." }
```

---

#### 2.17.6 `POST /api/system/restore` — 从备份恢复

- **鉴权：** Admin

从 `backups/` 内指定备份恢复，复用「导入暂存 + 启动期换库」：把所选备份的 `pmm.db` 写到 `.import-pending` + 合并密钥环，下次完整退出重启时原子换库（旧库自动备份为 `pmm.db.preimport-*`）。`fileName` 严格校验防路径穿越。

**请求体：**

```json
{ "fileName": "pmm-backup-20260606040000.zip" }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "success": true, "stagedPath": "...\\pmm.db.import-pending", "requiresRestart": true, "message": "导入包已暂存，请完整退出并重启应用…" }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"备份文件名非法"`（路径穿越守卫触发）
- `1000` `"指定的备份文件不存在"`
- `1000` `"导入包的 pmm.db 不是合法的 SQLite 文件"`
- `9000` `"文件 IO 失败"`

---

## 二·补、漂移补全章节（2026-06-05 新增；2026-06-12 已与 §二 主体统一回改，无新旧双轨）

> 下列章节覆盖早期草案缺失、但已上线的端点。

---

### 2.18 媒体库（Library）

> 控制器 `LibraryController`，`[Route("library")]` → 实际前缀 `/api/library`。
> 已归档作品按 TMDB 作品聚合的海报墙 + 富化关联（演职员/类型/公司/电视台/关键词/分季分集）+ 库内搜索 + 图片代理。
> **浏览类只读端点匿名可访问**（需求 §3.12）；刷新元数据 / 存在性扫描为写操作，需登录（`[Authorize]`）。
> 文件级写操作（重试/重新处理/删除/整剧）**复用 `/api/history/*` 端点**，本控制器不重复提供。

#### 2.18.1 `GET /api/library` — 媒体库列表（海报墙）

- **鉴权：** 否（匿名）

**查询参数：**

| 参数 | 类型 | 说明 |
|---|---|---|
| `type` | string | `movie` / `tv` 过滤 |
| `categoryId` | int | 分类筛选 |
| `keyword` | string | 标题模糊匹配 |
| `genreId` / `personId` / `companyId` / `networkId` / `keywordId` | string | 按富化关联实体过滤 |
| `country` / `language` | string | 出品国 / 语言 |
| `yearFrom` / `yearTo` | int | 年份区间 |
| `sort` | string | `recent`（默认，最近归档倒序）/ `year` / `rating` / `title` |
| `page` / `pageSize` | int | 分页，`pageSize` ≤ 100 |

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "items": [
      {
        "tmdbId": 27205, "mediaType": "movie", "title": "盗梦空间", "year": 2010,
        "rating": 8.4, "categoryName": "电影",
        "fileCount": 1, "missingFileCount": 0,
        "latestArchivedAt": "2026-05-16T02:00:00Z",
        "hasPoster": true, "genres": ["科幻", "动作"]
      }
    ],
    "total": 128, "page": 1, "pageSize": 24
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"分页参数非法"`

---

#### 2.18.2 `GET /api/library/facets` — 筛选项

- **鉴权：** 否（匿名）

**说明：** 返回库内已富化作品出现过的 类型/演职员/公司/电视台/关键词/国家/语言（各含 `id`、`name`、`count`），供前端筛选下拉。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "genres":    [ { "id": "18", "name": "剧情", "count": 42 } ],
    "persons":   [],
    "companies": [],
    "networks":  [],
    "keywords":  [],
    "countries": [ { "id": "CN", "name": "CN", "count": 30 } ],
    "languages": []
  },
  "requestId": "..."
}
```

---

#### 2.18.3 `GET /api/library/{tmdbId}` — 作品详情

- **鉴权：** 否（匿名）
- **路径：** `{tmdbId}`（int）
- **查询参数：** `mediaType`（`movie`/`tv`，缺省 `tv`）

**说明：** 打开时惰性富化（TMDB 失败降级返回已有数据）；返回富化元数据 + 演职员/公司/类型/关键词/季摘要 + 计数汇总 + 全部文件记录（含实时存在性 `fileExists`）。

**成功响应（节选）：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "tmdbId": 27205, "mediaType": "movie", "title": "盗梦空间", "year": 2010,
    "overview": "...", "rating": 8.4,
    "genres": [], "credits": [], "companies": [], "keywords": [], "seasons": [],
    "files": [ { "id": 123, "fileName": "...", "status": "Completed", "fileExists": true } ]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"无效的 TmdbId"`
- `1000` `"作品不存在或无记录"`

---

#### 2.18.4 `GET /api/library/{tmdbId}/seasons/{seasonNumber}` — 分季分集

- **鉴权：** 否（匿名）
- **路径：** `{tmdbId}`（int）/ `{seasonNumber}`（int）
- **查询参数：** `mediaType`（缺省 `tv`）

**说明：** 首次展开惰性拉取 `/tv/{id}/season/{n}`；返回该季每集（集名/简介/剧照/时长/评分）+ 本地文件映射（`hasLocalFile` / `localStatus` / `localFileExists`）。

**成功响应（节选）：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "seasonNumber": 1,
    "episodes": [
      { "episodeNumber": 1, "name": "...", "overview": "...", "stillPath": "...", "runtime": 45, "rating": 8.1,
        "hasLocalFile": true, "localStatus": "Completed", "localFileExists": true }
    ]
  },
  "requestId": "..."
}
```

---

#### 2.18.5 `GET /api/library/{tmdbId}/related` — 相关作品

- **鉴权：** 否（匿名）
- **路径：** `{tmdbId}`（int）
- **查询参数：** `mediaType`（缺省 `tv`）

**说明：** 按共享 类型/演职员/公司 计分，**仅返回库内已归档作品**，计分倒序。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    { "tmdbId": 157336, "mediaType": "movie", "title": "星际穿越", "year": 2014, "hasPoster": true, "score": 5 }
  ],
  "requestId": "..."
}
```

---

#### 2.18.6 `POST /api/library/{tmdbId}/refresh-metadata` — 刷新单部

- **鉴权：** 是（登录用户）
- **路径：** `{tmdbId}`（int）
- **查询参数：** `mediaType`（缺省 `tv`）

**请求体：** 空

**说明：** 强制重新拉 TMDB 富化数据覆盖 `Media_Work` 及关联、清空分集缓存。

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": true, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"未配置 TMDB ApiKey"`
- `1000` `"TMDB 调用失败"`

---

#### 2.18.7 `POST /api/library/refresh-metadata-all` — 整库刷新

- **鉴权：** 是（登录用户）

**请求体：** 空

**说明：** 对全部已归档作品批量富化（命中 TTL 未过期的跳过），供库内搜索覆盖历史作品；返回处理作品数。

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": 42, "requestId": "..." }
```

---

#### 2.18.8 `POST /api/library/scan-existence` — 存在性扫描

- **鉴权：** 是（登录用户）

**请求体：** 空

**说明：** 对全部已完成记录执行 `File.Exists(TargetPath)`，落 `FileMissing` / `FileCheckedAt`，供列表/详情打缺失标记。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "checked": 216, "missing": 3 },
  "requestId": "..."
}
```

---

#### 2.18.9 `GET /api/library/poster/{tmdbId}` — 作品海报

- **鉴权：** 否（匿名；`<img>` 无法带 token）
- **路径：** `{tmdbId}`（int）

**响应：** 二进制图片流，`Content-Type: image/jpeg`，源自本地缓存 `AppPaths.PostersDir/{tmdbId}.jpg`。

- **200** — 海报字节流
- **404** — 无缓存海报（前端 `<img onerror>` 降级占位）

> 注意：此端点失败走 HTTP 404，**不**包统一 JSON 信封（图片端点例外）。

---

#### 2.18.10 `GET /api/library/tmdb-image/{size}/{*path}` — 图片代理

- **鉴权：** 否（匿名）
- **路径：** `{size}`（白名单尺寸 `w92`/`w300`/`w500`/`original` 等）/ `{*path}`（TMDB 图片名，catch-all，无需前导斜杠）

**说明：** 按 TMDB 相对路径取背景图/人物照/剧照/Logo，后端按需缓存后流式返回（隐私：浏览器不直连 TMDB）。`Content-Type` 按扩展名推断（jpeg/png/svg/webp）。

- **200** — 图片字节流
- **404** — 非法尺寸/路径或拉取失败

---

### 2.19 媒体扩展名（Media Extensions）

> 控制器 `MediaExtensionsController`，`[Route("settings/media-extensions")]` → `/api/settings/media-extensions`。
> `System_MediaExtension` CRUD；扩展名必须以 `.` 开头、小写归一化、唯一约束。
> 变更后 `IMediaExtensionProvider` 内存缓存自动刷新，`FileWatcherWorker` / `ScanService` 立即生效。**仅 Admin。**
> **写操作的 id 在请求体**（非路径）。

#### 2.19.1 `GET /api/settings/media-extensions` — 列表

- **鉴权：** Admin

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": [
    { "id": 1, "extension": ".mkv", "description": "内置默认", "enabled": true, "createdAt": "...", "updatedAt": "..." }
  ],
  "requestId": "..."
}
```

> `data` 为数组（非分页信封），按 `extension` 字母排序。

---

#### 2.19.2 `POST /api/settings/media-extensions` — 新增

- **鉴权：** Admin

**请求体：**

```json
{ "extension": ".hevc", "description": "HEVC 高效视频编码", "enabled": true }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": { "id": 5, "extension": ".hevc", "description": "HEVC 高效视频编码", "enabled": true, "createdAt": "...", "updatedAt": "..." }, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"扩展名不能为空"`
- `1000` `"扩展名必须以 . 开头"`
- `1000` `"扩展名含非法字符"`
- `1000` `"扩展名已存在"`
- `1000` `"无操作权限"`

---

#### 2.19.3 `POST /api/settings/media-extensions/update` — 更新

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1, "extension": ".mkv", "description": "新备注", "enabled": false }
```

**成功响应：** 同新增（返回更新后的实体）。

**可能的错误 message：**

- `1000` `"扩展名不存在"`
- `1000` `"扩展名必须以 . 开头"`
- `1000` `"扩展名已存在"`
- `1000` `"无操作权限"`

---

#### 2.19.4 `POST /api/settings/media-extensions/delete` — 删除

- **鉴权：** Admin
- **id 在请求体**

**请求体：**

```json
{ "id": 1 }
```

**成功响应：**

```json
{ "code": 0, "message": "ok", "data": null, "requestId": "..." }
```

**可能的错误 message：**

- `1000` `"扩展名不存在"`
- `1000` `"无操作权限"`

---

### 2.20 AI 调用监控（AI Providers — Stats / Logs）

> 控制器 `ParseAiProvidersController`（与 §2.13 同一控制器，本节补充其**监控类端点**）。
> 聚合 `Audit_AiCall` 表，供失败诊断与升级链还原。**仅 Admin。**
> CRUD / test / enable 见 §2.13（update/delete/test/enable 的 id 在请求体；本节 stats/logs 的 id 在路径）。

#### 2.20.1 `GET /api/settings/ai-providers/{id}/stats` — 调用统计

- **鉴权：** Admin
- **路径：** `{id}`（long，提供商 ID）
- **查询参数：** `windowHours`（默认 24，范围 1–168）

**说明：** 聚合调用次数、成功/失败数、平均 / P95 延迟（仅 `Success` 计入）、小时桶分布、固定延迟直方图、错误类型分布、最近 12 条。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "providerId": 1,
    "windowHours": 24,
    "totalCalls": 120,
    "successCount": 108,
    "failedCount": 12,
    "avgLatencyMs": 820.4,
    "p95LatencyMs": 2100,
    "hourlyBuckets":   [ { "hoursAgo": 0, "total": 5, "failed": 1 } ],
    "latencyHistogram": [ { "bucket": "0-500ms", "count": 30 } ],
    "errorBreakdown":   [ { "errorType": "LowConfidence", "count": 8 } ],
    "recentCalls":      [ { "id": 9001, "success": false, "latencyMs": 820, "timestamp": "..." } ]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"提供商不存在"`
- `1000` `"windowHours 越界"`
- `1000` `"无操作权限"`

---

#### 2.20.2 `GET /api/settings/ai-providers/{id}/logs` — 调用日志（分页）

- **鉴权：** Admin
- **路径：** `{id}`（long，提供商 ID）

**查询参数：**

| 参数 | 类型 | 说明 |
|---|---|---|
| `page` | int | 默认 1 |
| `pageSize` | int | 默认 20，钳制 [1,100] |
| `success` | bool | 仅成功 / 仅失败 |
| `errorType` | string | 错误类型过滤 |
| `chainId` | string | 升级链 ID（还原一次解析的主备调用链） |
| `from` / `to` | string | ISO8601 时间区间 |

**说明：** 返回调用日志，**含请求 / 响应原文**（`requestText` / `responseText`，供失败诊断与升级链还原）。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "providerId": 1, "page": 1, "pageSize": 20, "total": 120,
    "items": [
      {
        "id": 9001, "mediaItemId": 42, "success": false, "latencyMs": 820,
        "errorType": "LowConfidence", "errorDetail": "...",
        "model": "deepseek-chat", "promptTokens": 210, "completionTokens": 36,
        "confidence": 0.55, "httpStatus": 200,
        "chainId": "ab12...", "attemptLevel": 1, "isPrimary": true,
        "requestText": "文件名：...", "responseText": "{...}",
        "timestamp": "..."
      }
    ]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"提供商不存在"`
- `1000` `"无操作权限"`

---

### 2.21 历史 — 批量重试失败（History rescan-failed）

> 补 §2.6 `HistoryController` 缺失的批量重试端点。

#### 2.21.1 `POST /api/history/rescan-failed` — 批量重试失败记录

- **鉴权：** Admin

**请求体：** 空

**说明：** 把所有 `Failed` 记录重置回 `Queued` 并重入主管线；源文件已不存在的跳过。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "requeued": 12, "skipped": 2 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"无操作权限"`
- `9000` `"批量重试失败"`

> 同控制器另有已上线端点（本次未逐一展开，详见 Scalar）：`GET /api/history/pending`（处理队列）、`GET /api/history/{id}`（详情）、`GET /api/history/{id}/nfo`、`GET /api/history/{id}/poster`、`POST /api/history/{id}/preview-archive`、`POST /api/history/{id}/manual-archive`、`POST /api/history/{id}/reopen`（重开回待确认队列）、`POST /api/history/reprocess`、`POST /api/history/delete`、`POST /api/history/{id}/undo-archive`（撤销归档，仅 Completed：归档文件移回源位 / 删副本，记录回退 Skipped）。

---

### 2.22 队列 — 预览/详情/绑定补全（Review）

> 补 §2.5 `ReviewController` 缺失端点。`tmdb-search` 的查询键为 `query`（控制器形参为避免同名前缀绑定冲突而命名 `criteria`，HTTP 查询键不受影响），§2.5.5 已按实际书写。

#### 2.22.1 `GET /api/review/{id}/tmdb-detail` — 按 ID 取 TMDB 详情

- **鉴权：** 是（Admin；与 `tmdb-search` 同理，代理外部 TMDB 耗 ApiKey 额度，收紧为需登录防匿名刷额度）
- **路径：** `{id}`（long，记录 ID）
- **查询参数：** `tmdbId`（必填）/ `mediaType`（`movie`/`tv`，必填）

**说明：** 人工填 TMDB ID 后的预览：拉标题/年份/海报/季数，确认无误后再走 `confirm` / `bind-tmdb`。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "tmdbId": 27205, "mediaType": "movie", "title": "盗梦空间",
    "originalTitle": "Inception", "year": 2010, "posterUrl": "...",
    "totalSeasons": null, "originCountry": ["US"]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"记录不存在"`
- `1000` `"mediaType 取值非法（应为 movie/tv）"`
- `1000` `"TMDB ID 无效或无法查询"`

---

#### 2.22.2 `POST /api/review/preview-paths` — 批量去向预览

- **鉴权：** 否（匿名，属「看队列」范畴，只读）

**请求体：**

```json
{
  "items": [
    { "key": "200", "tmdbId": 27205, "mediaType": "movie", "title": "盗梦空间", "year": 2010,
      "season": null, "episode": null, "episodeEnd": null, "categoryId": 1, "fileName": "Inception.2010.mkv" }
  ]
}
```

**说明：** 按各项 tmdbId/标题/年份/季集 + 分类 `TargetRoot` 算确认后的媒体库落点（按 Plex 媒体服务器命名惯例 `标题 (年份) {tmdb-ID}`，与确认同命名规范）。单项字段不全则该项 `error` 非空、`relativePath`/`fullPath` 为 null。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "entries": [
      { "key": "200",
        "relativePath": "盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv",
        "fullPath": "D:/Movies/...", "error": null }
    ]
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"items 不能为空"`
- `1000` `"items 数量超过上限 500"`

---

#### 2.22.3 `POST /api/review/batch-ignore` — 批量忽略

- **鉴权：** Admin

**请求体：**

```json
{ "items": [ { "id": 200, "rowVersion": 3 } ], "reason": "重复样本" }
```

**成功响应（部分失败也返 200 + code=0）：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "succeeded": [200], "failed": [ { "id": 201, "message": "记录已被其他用户修改，请刷新" } ] },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"items 不能为空"`
- `1000` `"items 数量超过上限 50"`

---

#### 2.22.4 `POST /api/review/check-files` — 文件存在性校验

- **鉴权：** Admin

**请求体：**

```json
{ "items": [ { "id": 200, "rowVersion": 3 } ] }
```

**说明：** 校验各项源文件是否仍存在；不存在的转 `Ignored` 移出待确认队列（保留记录 + 原因），存在的保留。

**成功响应（部分失败也返 200 + code=0）：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "removed": [200], "kept": 3, "failed": [ { "id": 201, "message": "记录已被其他用户修改，请刷新" } ] },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"items 不能为空"`
- `1000` `"items 数量超过上限 500"`

> `bind-tmdb`（§2.5.6）/ `tmdb-search`（§2.5.5）已在 §二有小节。

---

### 2.23 扫描 — 路径整理与演练（Scan path / dry-run）

> 补 §2.8 `ScanController` 缺失端点。整个控制器 `[Authorize(Roles=Admin)]`。

#### 2.23.1 `POST /api/scan/path` — 手动整理指定路径

- **鉴权：** Admin

**请求体：**

```json
{ "path": "F:/Downloads/movie.mkv" }
```

**说明：** 路径可为单个视频文件或目录（目录递归枚举视频文件）；**不必是已配置的监控目录**。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": { "scanId": "...", "path": "F:/Downloads", "isDirectory": true, "filesEnqueued": 3, "skipped": 1 },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"路径为空"`
- `1000` `"路径不存在"`
- `1000` `"不是受支持的视频文件"`
- `1000` `"已有扫描在进行中"`
- `9000` `"手动整理失败"`

---

#### 2.23.2 `POST /api/scan/dry-run` — 整理演练（只读）

- **鉴权：** Admin

**请求体：**

```json
{ "path": "F:/Downloads/盗梦空间.2010.1080p.mkv" }
```

**说明：** 只读演练：规则解析 + TMDB 匹配 + 媒体库命名预览（Plex 媒体服务器命名惯例），**不调用 AI、不移动文件、不写处理记录**。

**成功响应：**

```json
{
  "code": 0,
  "message": "ok",
  "data": {
    "path": "...", "fileName": "...", "title": "盗梦空间", "year": 2010, "mediaType": "movie",
    "outcome": "WouldArchive", "outcomeReason": "...",
    "tmdbQueried": true, "candidates": [], "picked": {},
    "previewRelativePath": "盗梦空间 (2010) {tmdb-27205}/盗梦空间 (2010) {tmdb-27205}.mkv",
    "previewNote": "..."
  },
  "requestId": "..."
}
```

**可能的错误 message：**

- `1000` `"路径为空"`
- `1000` `"无法解析文件名"`
- `9000` `"演练失败"`

> §2.8 旧小节的 `POST /api/scan/trigger` 实际支持 `?force=true` 查询参数（遇已存在 Failed 记录自动重投），响应额外含 `filesEnqueued` / `failedRescanned`；`POST /api/scan/folder/{id}` 响应额外含 `filesEnqueued`。

---

### 2.24 Webhook 端点修正注（已并回原节）

> 本节原为 2026-06-05 的漂移修正注；其内容（写操作请求体 id / `{id}/test` 测试发送 / 全局 `deliveries` 出站日志）已于 2026-06-12 全部并回 §2.16（见 §2.16.3–§2.16.7），不再单列。

---

## 三、SignalR Hub

### 3.1 `/hubs/logs` — 日志推送

- **鉴权：** 否（Hub 标注 `[AllowAnonymous]`，绕过全局 FallbackPolicy）
- **服务端推送事件：** `log`（Serilog → `SignalRSink` → `Clients.All.SendAsync("log", entry)`）

```json
{
  "level": "Information",
  "message": "检测到新文件：盗梦空间.2010.1080p.mkv，已入队等待处理",
  "timestamp": "2026-05-16T02:00:00.123Z",
  "source": "PersonalMediaManager.Host.HostedServices.FileWatcherWorker"
}
```

**说明：** 本 Hub **无任何客户端可调用方法**（纯服务端推送模式）；级别过滤由前端本地完成。

---

### 3.2 `/hubs/tasks` — 任务/队列推送

- **鉴权：** 否
- **服务端推送事件：**

`taskStatusChanged`：

```json
{ "id": 200, "status": "Archiving", "message": "正在归档" }
```

`queueChanged`：

```json
{ "reviewCount": 2, "todayProcessed": 12, "running": 1 }
```

---

## 四、错误码速查表

| code | 含义 | 触发场景示例 |
|---|---|---|
| `0` | Success | 业务成功 |
| `1000` | BusinessError | 鉴权失败、参数非法、记录不存在、并发冲突、状态不允许、外部 API 业务错误（如 TMDB Key 无效） |
| `9000` | ServerError | DB / 缓存 / 文件系统 / 外部网络等基础设施异常、未预期异常 |

> **新增 message 而非新增 code**：同类失败用同一 code + 不同 message。
> 前端基于 code 决定行为（跳转登录、提示重试、提示刷新…），基于 message 显示文案。

---

## 五、附：HTTP 谓词与路径动词对照

| 操作类别 | 路径模式 | 示例 |
|---|---|---|
| 列表 | `GET /api/<resource>` | `GET /api/settings/watch/folders` |
| 单例读取 | `GET /api/<singleton>` | `GET /api/settings/general` |
| 新增 | `POST /api/<resource>`（资源根，无 `/create` 段） | `POST /api/settings/watch/folders` |
| 修改（id 在请求体） | `POST /api/<resource>/update` | `POST /api/settings/watch/folders/update` |
| 删除（id 在请求体） | `POST /api/<resource>/delete` | `POST /api/settings/watch/folders/delete` |
| 单例更新 | `POST /api/<singleton>/update` | `POST /api/settings/general/update` |
| 动作（指定 ID，id 在路径） | `POST /api/<resource>/{id}/<action>` | `POST /api/review/{id}/confirm`、`POST /api/settings/webhooks/{id}/test` |
| 只读动作（指定 ID） | `GET /api/<resource>/{id}/<action>` | `GET /api/settings/watch/folders/{id}/test`、`GET /api/settings/ai-providers/{id}/stats` |
| 全局动作 | `POST /api/<resource>/<action>` | `POST /api/scan/trigger`、`POST /api/settings/tmdb/cache/clear` |

> 严格只用 `GET` 与 `POST`；写动作动词显式出现在路径末段，便于权限策略与路由扫描。
> 历史例外：account 资源沿用 `users/create`（显式 create 段）与 `users/{id}/delete`（id 在路径）。
