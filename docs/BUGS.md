# Bug List

## Open

### BUG-001: Search page "storage error" saat menggunakan PostgreSQL adapter

**Status**: Open  
**Severity**: Medium  
**Component**: `SearchService` → `SearchViaDedicatedProviderAsync` / `PostgreSqlQueryProvider`

**Deskripsi**:  
Halaman Search menampilkan "The search could not be completed due to a storage error." saat melakukan search dengan PostgreSQL storage adapter (`a2n.Hangfire.Dashboard.PostgreSql`).

**Root Cause (suspected)**:  
Exception dilempar oleh `PostgreSqlQueryProvider` saat menjalankan query (kemungkinan: tabel/kolom tidak ada, query syntax issue, atau connection timeout). Exception di-catch secara generic tanpa logging.

**Lokasi Kode**:
- `src/a2n.Hangfire.Dashboard/Services/SearchService.cs` → `SearchViaDedicatedProviderAsync()` (baris ~113)
- `src/a2n.Hangfire.Dashboard.PostgreSql/PostgreSqlQueryProvider.cs`

**Solusi yang Diusulkan**:
1. Tambahkan logging pada catch block untuk menampilkan actual exception
2. Implement fallback ke `GenericQueryProvider` (scan-based) jika dedicated provider gagal
3. Investigate query yang gagal di PostgreSQL (kemungkinan schema mismatch)

**Reproduce**:
1. Konfigurasi SampleApp dengan `StorageProvider=PostgreSql`
2. Buka halaman `/hangfire/search`
3. Ketik search term dan tekan Enter
4. Error "storage error" muncul

---

### BUG-002: Realtime graph pada halaman Dashboard tidak berjalan

**Status**: Open  
**Severity**: High  
**Component**: Home page realtime chart (Chart.js + chartjs-plugin-streaming)

**Deskripsi**:  
Realtime graph pada halaman Dashboard (Home) tidak berjalan/update. Sebelumnya berfungsi normal.

**Last Known Working Commit**: `5180528fd9f365b09cbf24359ea3354df199b0d3`

**Root Cause (suspected)**:  
Kemungkinan regression dari perubahan setelah commit tersebut. Perlu diff untuk identifikasi perubahan yang mempengaruhi chart JS interop atau SignalR broadcasting.

**Lokasi Kode**:
- `src/a2n.Hangfire.Dashboard/Components/Pages/Home.razor`
- `src/a2n.Hangfire.Dashboard/Content/js/charts.js`
- `src/a2n.Hangfire.Dashboard/Services/MetricsBroadcastService.cs`
- `src/a2n.Hangfire.Dashboard/Hubs/DashboardHub.cs`

**Solusi yang Diusulkan**:
1. Diff antara commit `5180528` dan HEAD untuk file terkait chart
2. Cek apakah JS interop call masih dipanggil dengan benar
3. Cek apakah SignalR hub masih broadcasting data
4. Cek browser console untuk JS errors

**Reproduce**:
1. Jalankan SampleApp
2. Buka halaman Dashboard (`/hangfire`)
3. Realtime chart tidak bergerak/update

---

### BUG-003: Analytics pages — realtime update pada timeframe "Last 1h" perlu diverifikasi

**Status**: Open  
**Severity**: Medium  
**Component**: Analytics pages (Overview, Performance, Failures, Queues, Recurring) + `AnalyticsBroadcastService`

**Deskripsi**:  
Sesuai requirement (ROADMAP Phase 2.4), ketika time range "Last 1h" dipilih pada halaman Analytics, data seharusnya di-update secara realtime via SignalR (5s interval) dengan badge "Live/Reconnecting" yang terlihat. Perlu diverifikasi apakah fitur ini masih berfungsi setelah perubahan terakhir.

**Requirement Reference** (ROADMAP.md, Phase 2.4):
- ✅ Realtime update via SignalR when range = "Last 1h" (Live/Reconnecting badges)
- ✅ AnalyticsBroadcastService (5s interval SignalR push)

**Lokasi Kode**:
- `src/a2n.Hangfire.Dashboard/Services/AnalyticsBroadcastService.cs`
- `src/a2n.Hangfire.Dashboard/Components/Pages/Analytics/*.razor`
- `src/a2n.Hangfire.Dashboard/Components/Shared/TimeRangeSelector.razor`
- `src/a2n.Hangfire.Dashboard/Hubs/DashboardHub.cs`

**Verify**:
1. Jalankan SampleApp dengan storage adapter (PostgreSQL/SQL Server)
2. Buka halaman Analytics (Overview, Performance, dll.)
3. Pilih timeframe "Last 1h"
4. Verifikasi badge "Live" muncul
5. Verifikasi data/chart ter-update setiap ~5 detik
6. Ganti ke timeframe lain (6h, 24h) — verifikasi realtime berhenti

---

## Resolved

(none yet)
