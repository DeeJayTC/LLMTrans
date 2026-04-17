<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { api } from '../api';

const route = useRoute();
const router = useRouter();
const styleRuleId = route.params.id as string;

type Instruction = { label: string; prompt: string; ordinal: number };

const instructions = ref<Instruction[]>([]);
const loading = ref(false);
const saving = ref(false);
const error = ref<string | null>(null);
const saved = ref<string | null>(null);

async function load() {
  loading.value = true;
  try { instructions.value = await api.styleRules.listInstructions(styleRuleId); }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { loading.value = false; }
}

function addRow() {
  if (instructions.value.length >= 10) return;
  instructions.value.push({
    label: '',
    prompt: '',
    ordinal: instructions.value.length,
  });
}

function removeRow(idx: number) {
  instructions.value.splice(idx, 1);
  renumber();
}

function moveUp(idx: number) {
  if (idx <= 0) return;
  const arr = instructions.value;
  const swap = arr[idx - 1];
  if (!swap) return;
  arr[idx - 1] = arr[idx]!;
  arr[idx] = swap;
  renumber();
}

function moveDown(idx: number) {
  if (idx >= instructions.value.length - 1) return;
  const arr = instructions.value;
  const swap = arr[idx + 1];
  if (!swap) return;
  arr[idx + 1] = arr[idx]!;
  arr[idx] = swap;
  renumber();
}

function renumber() {
  instructions.value.forEach((it, i) => { it.ordinal = i; });
}

const overLimit = computed(() => instructions.value.length > 10);
const anyTooLong = computed(() => instructions.value.some((i) => i.prompt.length > 300));

async function save() {
  if (overLimit.value || anyTooLong.value) return;
  saving.value = true;
  error.value = null;
  saved.value = null;
  try {
    await api.styleRules.setInstructions(styleRuleId,
      instructions.value.map((i, idx) => ({ ...i, ordinal: idx })));
    saved.value = 'Saved.';
    await load();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally {
    saving.value = false;
    setTimeout(() => { saved.value = null; }, 2500);
  }
}

onMounted(load);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center gap-2 text-sm text-surface-700">
      <button class="btn" @click="router.push('/style-rules')">← Back</button>
      <span>Style rule <span class="font-mono chip">{{ styleRuleId }}</span></span>
      <span class="ml-auto">{{ instructions.length }} / 10 instruction{{ instructions.length === 1 ? '' : 's' }}</span>
    </div>

    <div class="card">
      <div class="card-header">Custom instructions</div>
      <div class="card-body flex flex-col gap-3">
        <p class="text-sm text-surface-700">
          Up to 10 natural-language rules, each ≤ 300 characters. Passed verbatim to DeepL
          on every translate call as the <code>custom_instructions</code> array. Order
          matters — earlier instructions take precedence when DeepL hits a conflict.
        </p>

        <div v-for="(row, i) in instructions" :key="i" class="grid grid-cols-[30px_200px_1fr_auto] gap-2 items-start">
          <div class="text-xs text-surface-700 pt-2">{{ i + 1 }}.</div>
          <input
            class="input"
            v-model="row.label"
            placeholder="register"
          />
          <div class="flex flex-col gap-1">
            <textarea
              class="input font-mono text-xs"
              style="min-height: 60px; resize: vertical;"
              v-model="row.prompt"
              placeholder="Use business-formal register throughout."
            />
            <div class="text-xs text-right" :class="row.prompt.length > 300 ? 'text-red-700' : 'text-surface-700'">
              {{ row.prompt.length }} / 300
            </div>
          </div>
          <div class="flex flex-col gap-1 pt-1">
            <button class="btn text-xs" :disabled="i === 0" @click="moveUp(i)">↑</button>
            <button class="btn text-xs" :disabled="i === instructions.length - 1" @click="moveDown(i)">↓</button>
            <button class="btn btn-danger text-xs" @click="removeRow(i)">✕</button>
          </div>
        </div>

        <div v-if="instructions.length === 0" class="text-sm text-surface-700 py-2">
          No instructions yet — add one below.
        </div>

        <div class="flex items-center gap-2">
          <button class="btn" :disabled="instructions.length >= 10" @click="addRow">+ Add instruction</button>
          <span v-if="overLimit" class="text-xs text-red-700">Max 10 instructions.</span>
          <span v-if="anyTooLong" class="text-xs text-red-700">At least one instruction exceeds 300 chars.</span>
          <span class="ml-auto">
            <span v-if="saved" class="text-xs text-green-700 mr-2">{{ saved }}</span>
            <button class="btn-primary" :disabled="saving || overLimit || anyTooLong" @click="save">
              {{ saving ? 'Saving…' : 'Save' }}
            </button>
          </span>
        </div>

        <div v-if="error" class="text-xs text-red-700">{{ error }}</div>
      </div>
    </div>
  </div>
</template>
