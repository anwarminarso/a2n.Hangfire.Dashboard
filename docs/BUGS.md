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

## Resolved

(none yet)
