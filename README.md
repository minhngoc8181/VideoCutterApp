# Video Cutter

Cắt và ghép đoạn video **chính xác từng khung hình**, chỗ nối **không bị khựng / đứng hình**. Dùng libmpv để xem và FFmpeg để xuất.

## Cách dùng nhanh

1. Mở `VideoCutter.exe` → kéo thả video vào cửa sổ (hoặc **Ctrl+O**).
2. Đi tới chỗ cần cắt: kéo trên timeline, **← →** từng khung, **J K L** tua.
3. Đánh dấu:
   - **I** – *Giữ từ đây*: bỏ phần phía trước (trong đoạn hiện tại).
   - **O** – *Giữ đến đây*: bỏ phần phía sau, **giữ cả khung đang xem**.
   - **S** – chia đôi đoạn; **Delete** – bỏ / giữ đoạn tại đầu phát.
4. **P** xem thử kết quả (tự nhảy qua đoạn bỏ), **C** xem riêng chỗ nối (2 giây trước + sau).
5. Chọn chế độ xuất → **Xuất video** (Ctrl+E). File mới tên `…_cut.mp4` nằm cạnh file gốc; **file gốc không bị sửa**.

Kéo các vạch trắng trên timeline để chỉnh điểm cắt (tự bắt dính vào keyframe / ranh giới khung).
Ctrl + lăn chuột để phóng to timeline tới mức từng khung.

## Tinh chỉnh điểm cắt

- **Bấm giữ** một vạch cắt: vạch chuyển màu vàng (được chọn) và khung video **nhảy tới đúng chỗ đó**.
- **Kéo** vạch cắt: hình chạy theo vạch, thấy ngay khung sẽ được cắt.
  - Cuối một đoạn giữ → hiện **khung cuối cùng còn giữ**.
  - Đầu một đoạn giữ → hiện **khung đầu tiên được giữ**.
- **↑ / ↓** nhảy tới điểm cắt và chọn nó; **, / .** dịch điểm cắt đang chọn từng khung (Shift: 10 khung), hình chạy theo.
- Esc bỏ chọn. Mỗi lần chỉnh đều Ctrl+Z được.

## Danh sách đoạn

Mỗi thẻ đoạn cho biết: vị trí trong video gốc, **độ dài**, số khung, vị trí trong video xuất,
và một thanh nhỏ cho thấy đoạn nằm ở đâu trong cả video. Các nút quanh công tắc **Giữ**:

- **🔗 (trái)** – gộp với đoạn phía trên (khi 2 đoạn liền nhau về thời gian).
- **˄ ˅ (phải)** – chuyển đoạn lên / xuống: **video xuất ra theo thứ tự trong danh sách**.
  Số trên timeline là thứ tự xuất. Nút *Về thứ tự gốc* xếp lại theo thời gian.
- **🗑 (đỏ)** – xoá đoạn khỏi video xuất (Shift+Delete). Khác công tắc Giữ (chỉ tạm bỏ),
  đoạn bị xoá gộp luôn vào phần bỏ bên cạnh cho danh sách gọn. Ctrl+Z để hoàn tác.

Panel bên phải tự rộng ra theo phần màn hình mà video 16:9 không dùng tới.

Ô **Tổng phần giữ lại** ở cuối danh sách cho biết độ dài video kết quả, % so với gốc, phần bỏ, số chỗ nối.
Nhấn **P** để xem thử đúng theo thứ tự đã sắp xếp.

## Tự lưu phiên làm việc

Không cần bấm lưu hay xuất: mọi thao tác (cắt, giữ/bỏ, gộp, đổi thứ tự, xoá) được **tự lưu sau ~1 giây**,
khi đóng app và khi mở video khác. Góc trên hiện `• đã lưu 22:31:05`.

- Mở lại **đúng video đó** (kể cả đã đổi tên / chuyển thư mục) → tự khôi phục các đoạn, thứ tự và vị trí đang xem.
  **Ctrl+Z** ngay sau đó để bắt đầu lại từ video nguyên vẹn.
- Mỗi phiên gắn với video qua *dấu vân tay* (kích thước + mã băm 1 MB đầu/cuối file). Nếu file cùng tên
  nhưng đã bị ghi đè / nén lại, app **không áp dụng** phiên cũ để tránh lệch điểm cắt.
- Màn hình khởi động có mục **Tiếp tục làm việc** (6 phiên gần nhất).
- Phiên lưu ở `%AppData%\VideoCutter\sessions\` — không ghi thêm file nào vào thư mục video.
## Ba chế độ xuất

| Chế độ | Tốc độ | Chất lượng | Điểm cắt |
|---|---|---|---|
| **Chính xác – như gốc** (mặc định) | nhanh (GPU Intel/NVIDIA/AMD nếu có) | gần như gốc, giữ độ phân giải & fps | đúng từng khung |
| **Giữ gốc – siêu nhanh** | vài giây | 100% gốc (không mã hoá lại) | bắt vào keyframe → có thể dư một chút |
| **Chính xác – file nhỏ** | nhanh | nén mạnh hơn, chữ vẫn rõ | đúng từng khung |

Ở chế độ *Giữ gốc*, phần **tô vàng** trên timeline là phần dư sẽ có trong file (do phải bắt đầu/kết thúc tại keyframe).
Video quay bằng Screen Recorder 1.1 có keyframe ~10 giây/lần, nên với video đó hãy dùng chế độ *Chính xác*.

## Vì sao trước đây ghép video bị khựng 1–3 giây?

Khi cắt kiểu "copy" (không mã hoá lại) tại một điểm **không phải keyframe**, đoạn sau bắt đầu bằng các khung
không giải mã được cho tới keyframe kế tiếp → hình đứng yên 1–3 giây trong khi tiếng vẫn chạy. Video có B-frame còn
bị hụt vài khung ở chỗ nối. Video Cutter xử lý:

- *Chính xác*: mỗi đoạn được giải mã từ keyframe rồi cắt đúng khung, nối bằng bộ lọc concat → timestamp liên tục.
- *Giữ gốc*: điểm cắt luôn bắt vào keyframe, tính riêng mốc cắt cho hình (theo DTS) và tiếng (theo PTS).

Bộ tự kiểm tra (`VideoCutter.exe --selftest <thư mục>`) tạo video mẫu đánh số từng khung + tiếng bíp mỗi giây rồi kiểm tra:
đúng khung, không khung lặp (không đứng hình), timestamp không có lỗ, độ dài đúng, tiếng khớp hình.

## Phím tắt

| Phím | Chức năng |
|---|---|
| Space | Phát / dừng |
| J / K / L | Tua lùi / dừng / tua tới (bấm thêm = ×2, ×4, ×8) |
| ← / → | Lùi / tới 1 khung |
| Shift + ← / → | 1 giây |
| Ctrl + ← / → | 10 giây |
| Alt + ← / → | Keyframe trước / sau |
| ↑ / ↓ | Điểm cắt trước / sau (và chọn điểm cắt) |
| , / . | Dịch điểm cắt đang chọn 1 khung (Shift: 10 khung) |
| Home / End | Đầu / cuối video |
| I / O | Giữ từ đây / giữ đến đây |
| S hoặc Ctrl+B | Chia đoạn |
| Delete | Bỏ / giữ đoạn tại đầu phát |
| Shift+Delete | Xoá hẳn đoạn tại đầu phát |
| P | Xem thử kết quả |
| C | Xem chỗ nối gần nhất |
| M | Tắt / bật tiếng |
| Ctrl+Z / Ctrl+Y | Hoàn tác / làm lại |
| + / − / Ctrl+0 | Phóng to / thu nhỏ / xem toàn bộ timeline |
| Ctrl+O / Ctrl+E | Mở video / xuất video |
| F1 | Bảng phím tắt |

Ô thời gian: gõ `1:23.5`, `00:12:03.250`, `95` hoặc `#1234` (số khung) rồi Enter.
Bấm vào khung video để phát / dừng. Nhấp đúp một đoạn để phóng timeline vừa đoạn đó.

## Cài đặt

`VideoCutter.settings` (cạnh exe) lưu chế độ xuất, âm lượng, thư mục gần nhất.
Dòng `encoder=auto | gpu | cpu`: đặt `cpu` nếu muốn luôn dùng x264/x265 (chậm hơn, ổn định trên mọi máy).
Nhật ký lỗi: `VideoCutter.log`.

## Thành phần

- `VideoCutter.exe` – .NET Framework 4.8 (có sẵn trên Windows 10/11), 64-bit.
- `libmpv-2.dll` – trình phát (mpv, shinchiro build).
- `ffmpeg.exe`, `ffprobe.exe` – FFmpeg (gyan.dev build).

## Build từ mã nguồn

> **Lưu ý:** `ffmpeg.exe`, `ffprobe.exe`, `libmpv-2.dll` mỗi file > 100 MB nên **không có trong repository**
> (GitHub chặn file > 100 MB). Cần tải về thư mục `tools\` trước khi build.

**Cách 1 – tự động (khuyên dùng):**

```powershell
powershell -ExecutionPolicy Bypass -File .\download-deps.ps1
dotnet build -c Release
```

Script tải bản mới nhất, giải nén và chép đúng 3 file vào `tools\`. Thêm `-Force` để tải lại.
Giải nén `.7z` dùng 7-Zip nếu có, nếu không dùng `tar.exe` có sẵn của Windows.

**Cách 2 – tải tay** rồi chép vào `tools\`:

| File | Nguồn | Lấy file nào |
|---|---|---|
| `ffmpeg.exe`, `ffprobe.exe` | [gyan.dev – FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/) ([ffmpeg-release-essentials.zip](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip), bản sao: [GyanD/codexffmpeg](https://github.com/GyanD/codexffmpeg/releases)) | thư mục `bin\` trong file zip |
| `libmpv-2.dll` | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) (hoặc [SourceForge](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)) | `mpv-dev-x86_64-<ngày>-git-<mã>.7z` (không dùng bản `-v3` nếu CPU cũ) |

```
VideoCutterApp\
└─ tools\
   ├─ ffmpeg.exe
   ├─ ffprobe.exe
   └─ libmpv-2.dll
```

Khi build, 3 file này được tự chép ra cạnh `VideoCutter.exe`; nếu thiếu, build sẽ hiện cảnh báo.
Phiên bản đã thử: FFmpeg 9.0.2 essentials (gyan.dev), libmpv v0.41.0-1227 (shinchiro).

Đóng gói: `powershell -ExecutionPolicy Bypass -File .\make-dist.ps1` → `dist\VideoCutter.zip`
(tự chạy `download-deps.ps1` nếu `tools\` còn thiếu).
