<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { api } from '../api';

type Entry = {
  sourceLanguage: string;
  targetLanguage: string;
  sourceTerm: string;
  targetTerm: string;
  caseSensitive: boolean;
  doNotTranslate: boolean;
};

const route = useRoute();
const router = useRouter();
const glossaryId = route.params.id as string;

const entries = ref<Entry[]>([]);
const loading = ref(false);
const saving = ref(false);
const error = ref<string | null>(null);

// Draft form for adding new entries
const draft = ref<Entry>({
  sourceLanguage: 'en',
  targetLanguage: 'de',
  sourceTerm: '',
  targetTerm: '',
  caseSensitive: false,
  doNotTranslate: false,
});

// Bulk TSV input for importing several entries at once
const bulkInput = ref('');

async function load() {
  loading.value = true;
  try { entries.value = await api.glossaries.listEntries(glossaryId); }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { loading.value = false; }
}

async function addOne() {
  if (!draft.value.sourceTerm || (!draft.value.targetTerm && !draft.value.doNotTranslate)) return;
  saving.value = true;
  try {
    const payload: Entry = {
      ...draft.value,
      targetTerm: draft.value.doNotTranslate ? draft.value.sourceTerm : draft.value.targetTerm,
    };
    await api.glossaries.addEntries(glossaryId, [payload]);
    draft.value.sourceTerm = '';
    draft.value.targetTerm = '';
    draft.value.doNotTranslate = false;
    await load();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { saving.value = false; }
}

// File-based import (CSV / TBX). Goes through the dedicated import
// endpoint which handles the parsing server-side and reports
// added/skipped counts plus any per-line errors.
const importFormat = ref<'csv' | 'tbx'>('csv');
const importBody = ref('');
const importResult = ref<{ added: number; skipped: number; errors: string[] } | null>(null);

async function onImportFile(ev: Event) {
  const file = (ev.target as HTMLInputElement).files?.[0];
  if (!file) return;
  importBody.value = await file.text();
  importFormat.value = file.name.toLowerCase().endsWith('.tbx') ? 'tbx' : 'csv';
}

async function runImport() {
  if (!importBody.value.trim()) return;
  saving.value = true;
  importResult.value = null;
  try {
    importResult.value = await api.glossaries.importEntries(glossaryId, {
      format: importFormat.value,
      sourceLanguage: draft.value.sourceLanguage,
      targetLanguage: draft.value.targetLanguage,
      body: importBody.value,
      caseSensitive: draft.value.caseSensitive,
    });
    if (importResult.value.added > 0) await load();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { saving.value = false; }
}

async function importTsv() {
  const lines = bulkInput.value.split('\n').map((l) => l.trim()).filter(Boolean);
  const parsed: Entry[] = [];
  for (const line of lines) {
    const [source, target, flag] = line.split('\t');
    if (!source) continue;
    const dnt = (flag ?? '').toLowerCase() === 'dnt';
    parsed.push({
      sourceLanguage: draft.value.sourceLanguage,
      targetLanguage: draft.value.targetLanguage,
      sourceTerm: source,
      targetTerm: dnt ? source : (target ?? ''),
      caseSensitive: false,
      doNotTranslate: dnt,
    });
  }
  if (parsed.length === 0) return;
  saving.value = true;
  try {
    await api.glossaries.addEntries(glossaryId, parsed);
    bulkInput.value = '';
    await load();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { saving.value = false; }
}

onMounted(load);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center gap-2 text-sm text-surface-700">
      <button class="btn" @click="router.push('/glossaries')">← Back</button>
      <span>Glossary <span class="font-mono chip">{{ glossaryId }}</span></span>
      <span class="ml-auto">{{ entries.length }} entr{{ entries.length === 1 ? 'y' : 'ies' }}</span>
    </div>

    <!-- Add single entry -->
    <div class="card">
      <div class="card-header">Add entry</div>
      <form class="card-body grid grid-cols-6 gap-3 items-end" @submit.prevent="addOne">
        <label class="flex flex-col gap-1 text-xs">
          Source lang
          <input class="input" v-model="draft.sourceLanguage" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Target lang
          <input class="input" v-model="draft.targetLanguage" />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Source term
          <input class="input" v-model="draft.sourceTerm" placeholder="pull request" required />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Target term
          <input
            class="input"
            v-model="draft.targetTerm"
            :placeholder="draft.doNotTranslate ? '(matches source)' : 'Pull Request'"
            :disabled="draft.doNotTranslate"
          />
        </label>

        <label class="flex items-center gap-2 text-xs col-span-2">
          <input type="checkbox" v-model="draft.caseSensitive" /> case-sensitive
        </label>
        <label class="flex items-center gap-2 text-xs col-span-2">
          <input type="checkbox" v-model="draft.doNotTranslate" /> do not translate
        </label>
        <div class="col-span-2 text-right">
          <button class="btn-primary" type="submit" :disabled="saving">Add</button>
        </div>
      </form>
    </div>

    <!-- File import (CSV / TBX) -->
    <div class="card">
      <div class="card-header">
        <span>Import from file</span>
        <span class="text-xs text-surface-700">
          CSV (with <code>source,target</code> header row) or TBX 2008/3.0.
          Languages come from the form above.
        </span>
      </div>
      <div class="card-body flex flex-col gap-2">
        <div class="flex items-center gap-3">
          <label class="text-xs">
            <input type="radio" value="csv" v-model="importFormat" /> CSV
          </label>
          <label class="text-xs">
            <input type="radio" value="tbx" v-model="importFormat" /> TBX
          </label>
          <input type="file" accept=".csv,.tbx,.xml,.txt" @change="onImportFile"
                 class="text-xs" />
        </div>
        <textarea class="input font-mono text-xs" style="min-height: 100px;"
                  v-model="importBody"
                  placeholder="source,target&#10;pull request,Pull Request&#10;checkout,Kasse"></textarea>
        <div class="flex items-center gap-2">
          <button class="btn-primary" :disabled="saving || !importBody.trim()" @click="runImport">
            Import
          </button>
          <span v-if="importResult" class="text-xs text-surface-700">
            Added {{ importResult.added }} · skipped {{ importResult.skipped }}
            <span v-if="importResult.errors.length" class="text-red-700 ml-2">
              · {{ importResult.errors.length }} error(s): {{ importResult.errors[0] }}
            </span>
          </span>
        </div>
      </div>
    </div>

    <!-- Bulk import -->
    <div class="card">
      <div class="card-header">Bulk import (TSV)</div>
      <div class="card-body flex flex-col gap-2">
        <p class="text-xs text-surface-700">
          One entry per line: <code>source\ttarget[\tdnt]</code>. Add <code>dnt</code> in the third
          column to mark a term as do-not-translate. Language pair comes from the form above.
        </p>
        <textarea
          class="input font-mono"
          style="min-height: 140px;"
          v-model="bulkInput"
          placeholder="SQL Injection	SQL-Injection
DeepL	DeepL	dnt
checkout	Kasse"
        />
        <div class="text-right">
          <button class="btn-primary" @click="importTsv" :disabled="saving || !bulkInput.trim()">
            Import
          </button>
        </div>
      </div>
    </div>

    <!-- Listing -->
    <div class="card">
      <div class="card-header">Entries</div>
      <table>
        <thead>
          <tr>
            <th>Pair</th>
            <th>Source</th>
            <th>Target</th>
            <th>Flags</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(e, i) in entries" :key="i">
            <td class="text-xs">
              <span class="chip">{{ e.sourceLanguage }} → {{ e.targetLanguage }}</span>
            </td>
            <td class="font-mono text-xs">{{ e.sourceTerm }}</td>
            <td class="font-mono text-xs">{{ e.targetTerm }}</td>
            <td class="text-xs">
              <span v-if="e.doNotTranslate" class="chip-primary">do-not-translate</span>
              <span v-if="e.caseSensitive" class="chip ml-1">case-sensitive</span>
            </td>
          </tr>
          <tr v-if="entries.length === 0 && !loading">
            <td colspan="4" class="text-center py-6 text-surface-700">No entries yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="error" class="text-xs text-red-700">{{ error }}</div>
  </div>
</template>
