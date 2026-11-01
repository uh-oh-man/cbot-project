// pdbdump <pdb> class <Name>...   -> member layout (offset size name : type)
// pdbdump <pdb> global <regex>     -> data/function symbols whose name contains the text, with RVA
// pdbdump <pdb> enum <Name>        -> enum values
#include <windows.h>
#include <dia2.h>
#include <diacreate.h>
#include <cstdio>
#include <string>

// Minimal stand-in for ATL's CComPtr (ATL is not installed here).
template <class T> struct CComPtr {
  T* p = nullptr;
  CComPtr() {}
  ~CComPtr() { Release(); }
  CComPtr(const CComPtr&) = delete;
  void Release() { if (p) { p->Release(); p = nullptr; } }
  T** operator&() { return &p; }
  T* operator->() { return p; }
  operator T*() { return p; }
  explicit operator bool() const { return p != nullptr; }
};

static std::wstring TypeName(IDiaSymbol* t) {
  if (!t) return L"?";
  DWORD tag = 0; t->get_symTag(&tag);
  BSTR n = nullptr;
  if (tag == SymTagPointerType) {
    CComPtr<IDiaSymbol> b; t->get_type(&b); return TypeName(b) + L"*";
  }
  if (tag == SymTagArrayType) {
    CComPtr<IDiaSymbol> b; t->get_type(&b); DWORD c = 0; t->get_count(&c);
    return TypeName(b) + L"[" + std::to_wstring(c) + L"]";
  }
  if (tag == SymTagBaseType) {
    DWORD bt = 0; ULONGLONG len = 0; t->get_baseType(&bt); t->get_length(&len);
    switch (bt) {
      case btBool: return L"bool"; case btChar: return L"char"; case btFloat: return len == 8 ? L"double" : L"float";
      case btInt: case btLong: return L"int" + std::to_wstring(len * 8);
      case btUInt: case btULong: return L"uint" + std::to_wstring(len * 8);
      default: return L"base" + std::to_wstring(bt) + L"_" + std::to_wstring(len);
    }
  }
  if (t->get_name(&n) == S_OK && n) { std::wstring s(n); SysFreeString(n); return s; }
  return L"tag" + std::to_wstring(tag);
}

static void DumpClass(IDiaSymbol* global, const wchar_t* name, const std::wstring& prefix, LONG base) {
  CComPtr<IDiaEnumSymbols> e;
  if (FAILED(global->findChildren(SymTagUDT, name, nsCaseSensitive, &e))) return;
  CComPtr<IDiaSymbol> udt; ULONG got = 0;
  if (e->Next(1, &udt, &got) != S_OK) { wprintf(L"!! %s not found\n", name); return; }
  ULONGLONG len = 0; udt->get_length(&len);
  if (prefix.empty()) wprintf(L"== %s size=0x%llx\n", name, len);
  CComPtr<IDiaEnumSymbols> kids; udt->findChildren(SymTagNull, nullptr, nsNone, &kids);
  CComPtr<IDiaSymbol> k;
  while (kids && kids->Next(1, &k, &got) == S_OK && got) {
    DWORD tag = 0; k->get_symTag(&tag);
    if (tag == SymTagBaseClass) {
      CComPtr<IDiaSymbol> bt; k->get_type(&bt); LONG off = 0; k->get_offset(&off);
      std::wstring bn = TypeName(bt);
      wprintf(L"  %04X  [base %s]\n", base + off, bn.c_str());
    } else if (tag == SymTagData) {
      DWORD loc = 0; k->get_locationType(&loc);
      if (loc == LocIsThisRel || loc == LocIsBitField) {
        LONG off = 0; k->get_offset(&off); BSTR n = nullptr; k->get_name(&n);
        CComPtr<IDiaSymbol> ty; k->get_type(&ty); ULONGLONG sz = 0; if (ty) ty->get_length(&sz);
        wprintf(L"  %04X %4llu %s%s : %s\n", base + off, sz, prefix.c_str(), n ? n : L"?", TypeName(ty).c_str());
        if (n) SysFreeString(n);
      }
    }
    k.Release();
  }
}

int wmain(int argc, wchar_t** argv) {
  if (argc < 4) { wprintf(L"usage: pdbdump pdb class|global|enum names...\n"); return 1; }
  CoInitialize(nullptr);
  CComPtr<IDiaDataSource> src;
  HRESULT hr = NoRegCoCreate(L"msdia140.dll", CLSID_DiaSource, IID_IDiaDataSource, (void**)&src);
  if (FAILED(hr)) { wprintf(L"create failed %08x\n", hr); return 2; }
  if (FAILED(src->loadDataFromPdb(argv[1]))) { wprintf(L"load failed\n"); return 3; }
  CComPtr<IDiaSession> ses; src->openSession(&ses);
  CComPtr<IDiaSymbol> global; ses->get_globalScope(&global);
  std::wstring mode = argv[2];
  for (int i = 3; i < argc; i++) {
    if (mode == L"class") DumpClass(global, argv[i], L"", 0);
    else if (mode == L"enum") {
      CComPtr<IDiaEnumSymbols> e; global->findChildren(SymTagEnum, argv[i], nsCaseSensitive, &e);
      CComPtr<IDiaSymbol> en; ULONG got = 0;
      if (!e || e->Next(1, &en, &got) != S_OK) { wprintf(L"!! enum %s not found\n", argv[i]); continue; }
      wprintf(L"== enum %s\n", argv[i]);
      CComPtr<IDiaEnumSymbols> vals; en->findChildren(SymTagData, nullptr, nsNone, &vals);
      CComPtr<IDiaSymbol> v;
      while (vals && vals->Next(1, &v, &got) == S_OK && got) {
        BSTR n = nullptr; v->get_name(&n); VARIANT var; VariantInit(&var); v->get_value(&var);
        VariantChangeType(&var, &var, 0, VT_I4);
        wprintf(L"  %d %s\n", var.lVal, n); SysFreeString(n); v.Release();
      }
    } else if (mode == L"global") {
      for (DWORD tag : { (DWORD)SymTagData, (DWORD)SymTagFunction, (DWORD)SymTagPublicSymbol }) {
        CComPtr<IDiaEnumSymbols> e; global->findChildren((enum SymTagEnum)tag, argv[i], nsfRegularExpression | nsfCaseInsensitive, &e);
        CComPtr<IDiaSymbol> s; ULONG got = 0; int count = 0;
        while (e && e->Next(1, &s, &got) == S_OK && got && count++ < 40) {
          BSTR n = nullptr; s->get_name(&n); DWORD rva = 0; s->get_relativeVirtualAddress(&rva);
          CComPtr<IDiaSymbol> ty; s->get_type(&ty);
          wprintf(L"  tag%u rva=%08X %s : %s\n", tag, rva, n ? n : L"?", TypeName(ty).c_str());
          if (n) SysFreeString(n); s.Release();
        }
      }
    }
  }
  return 0;
}
