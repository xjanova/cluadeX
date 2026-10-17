<script setup>
import { computed } from 'vue'

const props = defineProps({ note: { type: Object, required: true } })
const emit = defineEmits(['update'])

const words = computed(() => (props.note.body.trim() ? props.note.body.trim().split(/\s+/).length : 0))
</script>

<template>
  <section class="editor">
    <input
      class="title-input"
      :value="note.title"
      placeholder="Title"
      @input="emit('update', { title: $event.target.value })"
    />
    <textarea
      class="body-input"
      :value="note.body"
      placeholder="Start writing…"
      @input="emit('update', { body: $event.target.value })"
    ></textarea>
    <footer class="editor-foot">
      <span>{{ words }} words</span>
      <span>Saved automatically</span>
    </footer>
  </section>
</template>
